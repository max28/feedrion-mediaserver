# Feedrion for Jellyfin & Emby

Plugins that connect a Jellyfin or Emby server to the **Séries** (TV shows)
extension of [Feedrion](https://feedrion.com).

## What you get

- **Scrobbling** — an episode you play past your threshold (90 % by default,
  set in the Feedrion app) is marked watched in Feedrion. Marking an episode
  played or unplayed on the server does the same.
- **History import** — when a user is linked, the episodes already played on
  the server are sent once.
- **Available on this server** — Feedrion shows which of your episodes this
  server holds, with an optional link that opens the episode here.
- **The other way round** — what you watched elsewhere (in the Feedrion app,
  Trakt, Simkl, another server…) is marked played on the server.
- **Multi-user** — every server user links their own Feedrion account.
- **You decide what reaches Feedrion** — per user: whether series it does not
  follow yet get added (off by default), whether an "unplayed" mark is sent,
  and which libraries are linked; server-wide: a list of series that are never
  sent at all. A series removed in the Feedrion app is never added back by the
  server.
- **Activity** — the last 50 changes exchanged with Feedrion, with the way each
  went, on the plugin page and in the Feedrion app.

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

1. Dashboard ▸ Plugins ▸ **Feedrion** ▸ **Users** tab ▸ *Link* next to the user.
2. A code appears (`ABCD-EFGH`). **That user** opens the Feedrion app ▸
   Settings ▸ Integrations ▸ Connected devices, and types it (or opens the link
   shown). The app shows what is asking before anything is approved.
3. The page says *Linked*. Unlink from the same page, or revoke the server from
   the app — both end the session.

## Settings

### In the Feedrion app (per reader)

Séries ▸ Settings ▸ Jellyfin / Emby:

- **Scrobble threshold** — how far an episode must be played to count
  (50–100 %, 90 % by default). "Played to the end" on the server always counts.
- **Follow the shows in my library** — off by default. On, every series this
  server holds (in the linked libraries, minus the ignored ones) is added to
  "Mes séries".

Settings ▸ Integrations ▸ Connected devices lists the linked servers, lets you
disconnect one, and opens **Recent activity**: the last 50 changes, both ways,
including the watches Feedrion skipped and why (series not followed, or
removed).

### On the plugin page (server admin)

Dashboard ▸ Plugins ▸ **Feedrion** has five tabs.

**Options** — pick a server user (linked or not yet: they can be set before
linking), then:

- **Add to Feedrion the series this user watches that it does not follow yet**
  — off by default. Off, a watch only counts for series already in
  "Mes séries"; the others are left out (the history import too). Keep it off
  on a profile the family shares. To bring in a series anyway, follow it in the
  app first.
- **Never mark an episode unwatched in Feedrion** — off by default. On, an
  episode marked unplayed on this server stays watched in Feedrion.
- **Linked libraries** — all of them by default (libraries added later
  included), or only the ones ticked. Everything follows that choice: the
  history import, playbacks, "available on this server", and the watches
  marked played from Feedrion.

Changing the libraries or "add new series" sends the library and the watch
history again at the next pass; Feedrion skips the watches it already has.

**Users** — *Link* / *Unlink* each server user (see above), the state of each
link, and **Sync now**.

**Ignored series** — server-wide: find a series of this server by name and
ignore it. Nothing about it is sent to any Feedrion account, ever (no watch, no
"available"), and nothing about it is marked played here.

**Activity** — the last 50 changes, newest first, with an arrow for the way
each went (`Jellyfin → Feedrion`, `Feedrion → Jellyfin`): series, episode,
user, time. Kept in the plugin's data folder, names included; nothing of it is
sent. It shows what the plugin sent; whether Feedrion then skipped a watch is
in the app's Recent activity.

**Server** — the public address of this server (optional: it turns "available
on this server" into a link, opened in the reader's own browser), the minutes
between two passes (a playback is sent within seconds anyway), and the Feedrion
API address (leave the default).

### Removing a series

A series that should not be in "Mes séries" (one added before these options
existed, say) is removed in the Feedrion app. From then on no media server adds
it back on its own — following it again in the app is the only way back. To
stop a series from ever being sent, add it to **Ignored series**.

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
