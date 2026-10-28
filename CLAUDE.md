# Feedrion for Jellyfin & Emby — notes for Claude

This repository WILL BE PUBLIC, history included. Write every file, commit
message and comment as if it already were.

## Never commit
- Secrets of any kind: tokens, keys, `.env` values, a real refresh token in a
  test fixture (fixtures use obviously fake strings).
- Infrastructure details of the Feedrion service: server names or paths,
  private hosts, VPN/mesh details, runner names, monitoring hosts, internal
  ports. The only Feedrion address in this repo is the public API
  (`https://api.feedrion.com/api`) and `https://feedrion.com`.
- Personal data, including the owner's e-mail address.
- Self-hosted runner labels hard-coded in a workflow. CI reads them from the
  `CI_RUNS_ON` repository variable, defaulting to GitHub-hosted; a public
  repository must run on GitHub-hosted runners (a PR from anyone would run on
  a self-hosted one). See `docs/PUBLISHING.md`.

## Layout
- `src/Feedrion.MediaServer.Core` — netstandard2.0, no server reference.
  `FeedrionClient` (API, token rotation), `LinkStore` (one JSON file per linked
  server user, atomic writes), `SyncEngine` (the pass), `Scrobble`.
  Compiled INTO each plugin (`<Compile Include=…>`), never shipped as its own
  DLL: a plugin is one assembly.
- `src/Jellyfin.Plugin.Feedrion` — Jellyfin 12.x, net10.0.
- `src/Emby.Plugin.Feedrion` — Emby 4.9, netstandard2.0.
- `tests/Feedrion.MediaServer.Core.Tests` — xunit, a fake Feedrion API.

## Rules that must not drift
- **Premium as a whole.** A 402 or `premium: false` sends nothing and DROPS
  nothing: events stay queued until the account is Premium again.
- **Only TheTVDB ids leave the server.** No title, file name, path or server
  user name in any payload. An episode without a TheTVDB id is ignored.
- **Per-link options belong to the server admin** (`LinkState.AddNewShows`,
  off by default, sent as `add_new_shows` with every events batch;
  `LinkState.LibraryIds`, null = every library). A choice of libraries filters
  everything: history import, live events, inventory, and the reverse
  `MarkPlayed`. With a choice made, an episode whose library is unknown is
  left out. A change resets `ImportDone` and `LibraryFullAt`.
- **Ignored series are server-wide** (`PluginConfiguration.IgnoredTvdbSeriesIds`,
  read through `SyncEngine.IgnoredSeries`): filtered at every exit — live
  events, history import, flush of what was queued before, inventory and
  library changes, and the reverse `MarkPlayed`. A change resets every link's
  `LibraryFullAt`.
- **`LinkState.NeverUnwatch`** (off by default): an unplayed mark is never
  queued, and one queued before the option is dropped at the next flush.
- **The activity log is LOCAL** (`ActivityLog`, `activity.json` beside the
  links, 50 entries): names come from the server (`EpisodeRef.SeriesName`,
  `[JsonIgnore]` fields on `WatchEvent` / `LibraryItem`) and are never sent.
  What was queued keeps its name in memory only (`SyncEngine._labels`); after a
  restart the last library listing names it, or nothing does.
- **The page has five tabs: Options ▸ Users ▸ Ignored series ▸ Activity ▸
  Server**, in both plugins. The Jellyfin page and the Emby script share their
  logic line for line; change both.
- **The threshold belongs to the reader**, read from
  `GET /integrations/v1/mediaserver/config`; the server's own "played to
  completion" always counts.
- **Echo suppression**: a watch pulled from Feedrion and marked played here
  must not be sent back (5-minute window, set BEFORE touching the server).
- **The last event about an episode wins** in the queue; the queue is bounded.
- **A 401 after one refresh means revoked**: forget the tokens, keep the link
  row so the admin page can say so.
- **https only** for the API address; plain http is refused.
- **Emby runs on .NET 8**: the Emby plugin compiles against System.Text.Json
  8.x and never higher (a 10.x build asked Emby 4.11 for an assembly it does
  not have, and the plugin never started). `scripts/package.py` refuses it;
  dependabot ignores its majors.
- The API contract lives in the Feedrion API (`/integrations/v1/*`). Changes
  there are additive; this client must tolerate unknown fields.

## Before pushing
`dotnet build Feedrion.MediaServer.slnx` (warnings are errors) and
`dotnet test tests/Feedrion.MediaServer.Core.Tests`.
