#!/usr/bin/env python3
"""Build the release artifacts from already-published plugin folders.

    python3 scripts/package.py --version 1.2.0 --out artifacts \
        --jellyfin publish/jellyfin --emby publish/emby \
        --repo-url https://github.com/<owner>/feedrion-mediaserver

Writes:
  feedrion-jellyfin-<v>.zip   the plugin DLL (Jellyfin's plugin layout)
  feedrion-emby-<v>.zip       the plugin DLL (copy into Emby's plugins folder)
  feedrion-thumb.png          the catalogue picture the manifest names (imageUrl)
  manifest.json               a Jellyfin plugin repository listing this version
  SHA256SUMS

Only the plugin's OWN assembly is packaged: everything else it references is
provided by the server at runtime, and shipping a second copy of a server
assembly is how a plugin breaks on the next server update.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import re
import shutil
import zipfile

JELLYFIN_GUID = "5f0c2a8e-6b1d-4c7e-9a3f-2d8e1b4c6a90"
# A fixed timestamp keeps the zips reproducible.
ZIP_DATE = (2024, 1, 1, 0, 0, 0)


def _zip(out: pathlib.Path, files: list[pathlib.Path]) -> None:
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for f in files:
            info = zipfile.ZipInfo(f.name, ZIP_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, f.read_bytes())


def _digest(path: pathlib.Path, algo: str) -> str:
    h = hashlib.new(algo)
    h.update(path.read_bytes())
    return h.hexdigest()


def _jellyfin_abi(csproj: pathlib.Path) -> str:
    match = re.search(r'Include="Jellyfin\.Controller"\s+Version="([0-9.]+)"', csproj.read_text())
    if not match:
        raise SystemExit("Jellyfin.Controller version not found in " + str(csproj))
    parts = (match.group(1).split(".") + ["0", "0", "0"])[:4]
    return ".".join(parts)


# Emby runs on .NET 8: an assembly compiled against a newer System.Text.Json
# asks for a version the server does not have, and the plugin never starts.
EMBY_MAX_STJ_MAJOR = 8


def _check_emby_references(csproj: pathlib.Path) -> None:
    m = re.search(r'Include="System\.Text\.Json"\s+Version="(\d+)\.', csproj.read_text())
    if m and int(m.group(1)) > EMBY_MAX_STJ_MAJOR:
        raise SystemExit(
            f"{csproj.name}: System.Text.Json {m.group(1)}.x is newer than what Emby's .NET "
            f"{EMBY_MAX_STJ_MAJOR} runtime provides; the plugin would not load"
        )


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--version", required=True)
    p.add_argument("--out", required=True, type=pathlib.Path)
    p.add_argument("--jellyfin", required=True, type=pathlib.Path)
    p.add_argument("--emby", required=True, type=pathlib.Path)
    p.add_argument("--repo-url", required=True)
    p.add_argument("--changelog", default="")
    a = p.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+", a.version):
        raise SystemExit("version must be X.Y.Z")
    a.out.mkdir(parents=True, exist_ok=True)
    root = pathlib.Path(__file__).resolve().parent.parent
    _check_emby_references(root / "src/Emby.Plugin.Feedrion/Emby.Plugin.Feedrion.csproj")

    jf_dll = a.jellyfin / "Jellyfin.Plugin.Feedrion.dll"
    emby_dll = a.emby / "Emby.Plugin.Feedrion.dll"
    for dll in (jf_dll, emby_dll):
        if not dll.is_file():
            raise SystemExit("missing " + str(dll))

    jf_zip = a.out / f"feedrion-jellyfin-{a.version}.zip"
    emby_zip = a.out / f"feedrion-emby-{a.version}.zip"
    _zip(jf_zip, [jf_dll])
    _zip(emby_zip, [emby_dll])
    # Jellyfin's catalogue shows the picture its manifest names; it ships with
    # the release so it never depends on a branch.
    thumb = a.out / "feedrion-thumb.png"
    shutil.copyfile(root / "assets/thumb.png", thumb)

    abi = _jellyfin_abi(root / "src/Jellyfin.Plugin.Feedrion/Jellyfin.Plugin.Feedrion.csproj")
    manifest = [{
        "guid": JELLYFIN_GUID,
        "name": "Feedrion",
        "overview": "Sync watched episodes with Feedrion's TV shows extension.",
        "description": (
            "Sends the episodes you watch to your Feedrion account, shows which episodes this "
            "server holds, and marks played what you watched elsewhere. Requires Feedrion Premium."
        ),
        "owner": "Feedrion",
        "category": "General",
        "imageUrl": f"{a.repo_url}/releases/download/v{a.version}/{thumb.name}",
        "versions": [{
            "version": a.version + ".0",
            "changelog": a.changelog or ("Release " + a.version),
            "targetAbi": abi,
            "sourceUrl": f"{a.repo_url}/releases/download/v{a.version}/{jf_zip.name}",
            # Jellyfin verifies the download against this (MD5 is what it reads).
            "checksum": _digest(jf_zip, "md5"),
            "timestamp": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }],
    }]
    (a.out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")

    sums = [f"{_digest(f, 'sha256')}  {f.name}" for f in sorted(a.out.glob("*")) if f.name != "SHA256SUMS"]
    (a.out / "SHA256SUMS").write_text("\n".join(sums) + "\n")
    print("\n".join(sums))


if __name__ == "__main__":
    main()
