# Feedrion for Jellyfin & Emby

Plugins that connect a Jellyfin or Emby server to the **Séries** (TV shows)
extension of [Feedrion](https://feedrion.com).

- **Scrobbling** — an episode you play past your threshold (90 % by default,
  set in the Feedrion app) is marked watched in Feedrion. Marking an episode
  played or unplayed on the server does the same.
- **History import** — when a user is linked, the episodes already played on
  the server are sent once.
- **Available on this server** — Feedrion shows which of your episodes this
  server holds, with an optional link that opens the episode here.
- **The other way round** — what you watched elsewhere (in the Feedrion app,
  Trakt, Simkl…) is marked played on the server.
- **Multi-user** — every server user links their own Feedrion account.

**Requires Feedrion Premium.** A linked account that is not Premium keeps its
events queued on the server and sends nothing until it is.

## Privacy

Only [TheTVDB](https://thetvdb.com) ids leave your server (series and episode
ids, how far a playback went, when), plus the server's name, version and — if
you set it — its public address. **Never** a title, a file name, a path, an IP
or a server user name. Feedrion never connects to your server: the plugin
always calls out, over https. Episodes without a TheTVDB id are ignored.

## Requirements

| Server | Version | Plugin |
|--------|---------|--------|
| Jellyfin | 12.3 or later (12.x) | `feedrion-jellyfin-<version>.zip` |
| Emby Server | 4.9 or later | `feedrion-emby-<version>.zip` |

Your libraries need TheTVDB metadata for TV shows (the default for both servers).

## Install

### Jellyfin

1. Dashboard ▸ Plugins ▸ Repositories ▸ **+**, URL:
   `https://github.com/max28/feedrion-mediaserver/releases/latest/download/manifest.json`
2. Catalogue ▸ **Feedrion** ▸ Install, then restart Jellyfin.

Or unzip `feedrion-jellyfin-<version>.zip` into `plugins/Feedrion_<version>/`
in Jellyfin's data folder and restart.

### Emby

Copy `Emby.Plugin.Feedrion.dll` from `feedrion-emby-<version>.zip` into Emby's
`plugins` folder and restart Emby.

## Link a user

1. Dashboard ▸ Plugins ▸ **Feedrion** ▸ *Link* next to the user.
2. A code appears (`ABCD-EFGH`). **That user** opens the Feedrion app ▸
   Settings ▸ Integrations ▸ Connected devices, and types it (or opens the link
   shown). The app shows what is asking before anything is approved.
3. The page says *Linked*. Unlink from the same page, or revoke the server from
   the app — both end the session.

The scrobble threshold and "follow the shows in my library" are per reader and
live in the app: Séries ▸ Settings ▸ Jellyfin / Emby.

The plugin page has five tabs: **Options**, **Users**, **Ignored series**,
**Activity**, **Server**.

**Options** — pick a server user (linked or not yet), then:

- **Add new series** — off by default. A watch of a series the Feedrion account
  does not follow yet is then left out, instead of adding the series to
  "Mes séries". Keep it off on a profile the family shares.
- **Never mark an episode unwatched in Feedrion** — off by default. An
  episode marked unplayed on this server then stays watched in Feedrion.
- **Linked libraries** — all of them by default (libraries added later
  included), or the ones ticked. Only those libraries' episodes are sent as
  watched or available, and only their episodes are marked played from
  Feedrion.

Changing either sends the library and the watch history again at the next
pass; Feedrion skips the watches it already has.

**Activity** — the last 50 changes exchanged with Feedrion, newest first, with
an arrow for the way each went (`Jellyfin → Feedrion`, `Feedrion → Jellyfin`).
Kept in the plugin's data folder, names included; nothing of it is sent. The
Feedrion app shows the same history from its side, including the watches it
skipped (Settings ▸ Integrations ▸ Connected devices ▸ Recent activity).

**Ignored series** — server-wide: find a series of this server by name and
ignore it. Nothing about it is sent to any Feedrion account, ever (no watch, no
"available"). A series already in "Mes séries" is removed in the Feedrion app;
once removed there, no media server adds it back on its own — following it
again in the app is the only way back.

## Verify a download

Every release asset is signed with [cosign](https://docs.sigstore.dev/) (keyless):

```bash
cosign verify-blob feedrion-jellyfin-1.0.0.zip \
  --bundle feedrion-jellyfin-1.0.0.zip.sigstore.json \
  --certificate-identity-regexp '^https://github.com/max28/feedrion-mediaserver/\.github/workflows/release\.yml@refs/heads/main$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

## Build

```bash
dotnet build Feedrion.MediaServer.slnx
dotnet test tests/Feedrion.MediaServer.Core.Tests
```

`src/Feedrion.MediaServer.Core` holds everything that is not server-specific
(API client, link store, sync engine); its sources are compiled into each
plugin so that each ships as a single DLL.

## License

[GPL-3.0](LICENSE). Feedrion is a trademark of its owner; this licence covers
the code in this repository, not the Feedrion service or its name.
