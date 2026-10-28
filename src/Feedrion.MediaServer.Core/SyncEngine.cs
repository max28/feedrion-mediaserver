using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Feedrion.MediaServer.Core
{
    /// <summary>An episode as the server knows it, reduced to TheTVDB ids.</summary>
    public sealed class EpisodeRef
    {
        public int TvdbSeriesId { get; set; }
        public int? TvdbEpisodeId { get; set; }
        public int? Season { get; set; }
        public int? Number { get; set; }

        /// <summary>The libraries the episode sits in; null when unknown.</summary>
        public IReadOnlyList<string>? LibraryIds { get; set; }

        /// <summary>The server's own names, for the local activity log only.</summary>
        public string? SeriesName { get; set; }
        public string? EpisodeName { get; set; }

        public string Key => TvdbEpisodeId.HasValue
            ? "e" + TvdbEpisodeId.Value
            : "s" + TvdbSeriesId + ":" + Season + ":" + Number;
    }

    /// <summary>What a media server must offer the engine. Jellyfin and Emby
    /// each implement it over their own API; nothing else differs.</summary>
    public interface IMediaServerAdapter
    {
        /// <summary>« jellyfin » or « emby » — the Feedrion integration kind.</summary>
        string Kind { get; }

        ServerInfo DescribeServer();

        /// <summary>The server's libraries, for the configuration page.</summary>
        IReadOnlyList<MediaLibrary> ListLibraries();

        /// <summary>Every series the server holds that carries a TheTVDB id.</summary>
        IReadOnlyList<MediaSeries> ListSeries();

        /// <summary>Every episode the server holds that carries TheTVDB ids,
        /// with the libraries it sits in.</summary>
        IReadOnlyList<LibraryItem> ListLibraryEpisodes();

        /// <summary>Every episode [serverUserId] has played — the initial import.</summary>
        IReadOnlyList<WatchEvent> ListPlayedEpisodes(string serverUserId);

        /// <summary>Mark an episode played for [serverUserId], and return it;
        /// null when the server does not hold it in a library [covers]
        /// accepts, or it is already played.</summary>
        LibraryItem? MarkPlayed(string serverUserId, RemoteWatch watch, Func<IReadOnlyList<string>?, bool> covers);

        /// <summary>An episode as the last library listing knew it — names
        /// included — without listing the library again; null if unknown.</summary>
        LibraryItem? Find(int tvdbSeriesId, int? tvdbEpisodeId, int? season, int? number);
    }

    public sealed class PendingLink
    {
        public string UserCode { get; set; } = "";
        public string VerificationUrl { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>The sync loop, shared by both plugins.</summary>
    public sealed class SyncEngine
    {
        public const int EventsPerCall = 500;
        public const int LibraryPerCall = 5000;
        public const int WatchesPerCall = 500;
        public const int WatchPagesPerPass = 10;
        public static readonly TimeSpan LibraryResendEvery = TimeSpan.FromHours(24);
        // A watch the engine itself set on the server comes back as a server
        // event a moment later; it is not news for Feedrion.
        private static readonly TimeSpan EchoWindow = TimeSpan.FromMinutes(5);

        private readonly IMediaServerAdapter _adapter;
        private readonly LinkStore _store;
        private readonly HttpClient _http;
        private readonly Func<string?> _apiBaseUrl;
        private readonly Action<string, Exception?> _log;
        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<string, PendingLink> _pending = new ConcurrentDictionary<string, PendingLink>();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _echoes = new ConcurrentDictionary<string, DateTimeOffset>();
        // Names of what is queued, keyed by user and episode, for the activity
        // log: the queue itself never carries a name (it is what is sent).
        private readonly ConcurrentDictionary<string, EpisodeRef> _labels = new ConcurrentDictionary<string, EpisodeRef>();
        private readonly ActivityLog _activity;
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim _running = new SemaphoreSlim(1, 1);

        public SyncEngine(
            IMediaServerAdapter adapter, LinkStore store, HttpClient http,
            Func<string?> apiBaseUrl, Action<string, Exception?> log)
        {
            _adapter = adapter;
            _store = store;
            _http = http;
            _apiBaseUrl = apiBaseUrl;
            _log = log;
            _activity = new ActivityLog(System.IO.Path.Combine(store.DirectoryPath, "activity.json"));
        }

        /// <summary>The last changes exchanged with Feedrion, newest first.</summary>
        public IReadOnlyList<ActivityEntry> Activity() => _activity.Latest();

        public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        /// <summary>The series nothing is ever sent about, for any user — the
        /// server-wide « ignored series » list (TheTVDB ids).</summary>
        public Func<IReadOnlyCollection<int>> IgnoredSeries { get; set; } = () => Array.Empty<int>();

        private bool IsIgnored(int tvdbSeriesId) => IgnoredSeries().Contains(tvdbSeriesId);

        /// <summary>The list changed: what is still queued about a newly ignored
        /// series is dropped at the next flush, and the inventory is sent again
        /// so Feedrion stops calling it « available ».</summary>
        public void IgnoredSeriesChanged()
        {
            lock (_gate)
            {
                foreach (var state in _store.All().Where(s => s.IsLinked))
                {
                    state.LibraryFullAt = null;
                    _store.Save(state);
                }
            }
            Wake();
        }

        public IReadOnlyList<MediaSeries> ListSeries() => _adapter.ListSeries();

        public IReadOnlyList<LinkState> Links => _store.All();

        public PendingLink? GetPending(string serverUserId) =>
            _pending.TryGetValue(serverUserId, out var p) && p.ExpiresAt > Clock() ? p : null;

        private FeedrionClient ClientFor(LinkState? state) =>
            new FeedrionClient(_http, _apiBaseUrl(), state == null ? null : new LinkSession(state, _store));

        // ── Linking ───────────────────────────────────────────────────────────

        /// <summary>Ask Feedrion for a code the reader types in the app, and
        /// wait for it in the background.</summary>
        public async Task<PendingLink> StartLinkAsync(string serverUserId, string? serverUserName, string? label, CancellationToken ct)
        {
            var start = await ClientFor(null).StartPairingAsync(_adapter.Kind, label, ct).ConfigureAwait(false);
            var pending = new PendingLink
            {
                UserCode = start.UserCode,
                VerificationUrl = start.VerificationUrl,
                ExpiresAt = Clock().AddSeconds(Math.Max(30, start.ExpiresIn)),
            };
            _pending[serverUserId] = pending;
            _ = Task.Run(() => PollAsync(serverUserId, serverUserName, start, pending), CancellationToken.None);
            return pending;
        }

        private async Task PollAsync(string serverUserId, string? serverUserName, PairingStart start, PendingLink pending)
        {
            var client = ClientFor(null);
            var interval = TimeSpan.FromSeconds(Math.Max(2, start.Interval));
            while (Clock() < pending.ExpiresAt && _pending.TryGetValue(serverUserId, out var current) && current == pending)
            {
                await Task.Delay(interval).ConfigureAwait(false);
                try
                {
                    var poll = await client.PollPairingAsync(start.DeviceCode, CancellationToken.None).ConfigureAwait(false);
                    if (poll.Status == "expired") break;
                    if (!poll.IsApproved) continue;
                    var state = _store.Get(serverUserId) ?? new LinkState { ServerUserId = serverUserId };
                    state.ServerUserName = serverUserName;
                    state.AccessToken = poll.AccessToken;
                    state.RefreshToken = poll.RefreshToken;
                    state.FamilyId = poll.FamilyId;
                    state.LinkedAt = Clock();
                    state.LastError = null;
                    // A new account starts from now: nothing it never asked for
                    // is replayed back at the server.
                    state.WatchCursor = null;
                    state.ImportDone = false;
                    state.ServerInfoSent = null;
                    state.LibraryFullAt = null;
                    _store.Save(state);
                    _pending.TryRemove(serverUserId, out _);
                    Wake();
                    return;
                }
                catch (FeedrionApiException e) when (e.IsTransient)
                {
                    // Keep waiting: the code is still valid.
                }
                catch (Exception e)
                {
                    pending.Error = e.Message;
                    _log("Feedrion: pairing failed", e);
                    return;
                }
            }
            _pending.TryRemove(serverUserId, out _);
        }

        /// <summary>Sign the link out on both sides. Feedrion being unreachable
        /// does not keep the link here: the reader can still revoke it in the app.</summary>
        public async Task UnlinkAsync(string serverUserId, CancellationToken ct)
        {
            var state = _store.Get(serverUserId);
            _pending.TryRemove(serverUserId, out _);
            if (state == null) return;
            if (state.IsLinked)
            {
                try
                {
                    await ClientFor(state).DisconnectAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log("Feedrion: could not tell Feedrion about the disconnection", e);
                }
            }
            _store.Remove(serverUserId);
        }

        // ── What the server tells the engine ──────────────────────────────────

        /// <summary>A playback stopped. Counted when it reached the reader's
        /// threshold; the percent travels so Feedrion holds it to the same rule.</summary>
        public void RecordPlayback(string serverUserId, EpisodeRef episode, double? percent, bool playedToCompletion)
        {
            var state = _store.Get(serverUserId);
            if (state == null || !state.IsLinked || IsEcho(serverUserId, episode.Key)) return;
            if (!state.Covers(episode.LibraryIds) || IsIgnored(episode.TvdbSeriesId)) return;
            if (!Scrobble.CountsAsWatched(percent, playedToCompletion, state.ScrobblePercent)) return;
            Enqueue(state, Event(WatchEvent.Watched, episode, playedToCompletion ? (double?)null : percent));
        }

        /// <summary>The reader marked an episode played / unplayed on the server.</summary>
        public void RecordPlayedToggle(string serverUserId, EpisodeRef episode, bool played)
        {
            var state = _store.Get(serverUserId);
            if (state == null || !state.IsLinked || !state.Covers(episode.LibraryIds)) return;
            if (IsIgnored(episode.TvdbSeriesId)) return;
            if (!played && state.NeverUnwatch) return;
            if (played && IsEcho(serverUserId, episode.Key)) return;
            Enqueue(state, Event(played ? WatchEvent.Watched : WatchEvent.Unwatched, episode, null));
        }

        /// <summary>Episodes arrived on, or left, the server — for every link
        /// whose libraries hold them. A removal is told to every link: an id
        /// Feedrion does not hold for it is a no-op there.</summary>
        public void RecordLibraryChange(IReadOnlyCollection<LibraryItem> added, IReadOnlyCollection<int> removed)
        {
            if (added.Count == 0 && removed.Count == 0) return;
            lock (_gate)
            {
                foreach (var state in _store.All().Where(s => s.IsLinked))
                {
                    var mine = added.Where(a => state.Covers(a.LibraryIds) && !IsIgnored(a.TvdbSeriesId)).ToList();
                    if (mine.Count == 0 && removed.Count == 0) continue;
                    state.EnqueueLibrary(mine, removed);
                    _store.Save(state);
                }
            }
            Wake();
        }

        /// <summary>The configuration page's settings for one server user. They
        /// may be set before the user is linked, and survive a re-link.
        /// A change re-sends the inventory and the history: what was left out
        /// before may be in now (Feedrion skips the watches it already has).</summary>
        public LinkState UpdateSettings(string serverUserId, string? serverUserName, LinkSettings settings)
        {
            var known = new HashSet<string>(_adapter.ListLibraries().Select(l => l.Id), StringComparer.OrdinalIgnoreCase);
            var libraries = settings.LibraryIds?
                .Where(id => !string.IsNullOrWhiteSpace(id) && known.Contains(id.Trim()))
                .Select(id => id.Trim().ToLowerInvariant())
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            LinkState state;
            lock (_gate)
            {
                state = _store.Get(serverUserId) ?? new LinkState { ServerUserId = serverUserId };
                if (state.ServerUserName == null) state.ServerUserName = serverUserName;
                var changed = state.AddNewShows != settings.AddNewShows
                    || !SameLibraries(state.LibraryIds, libraries);
                state.AddNewShows = settings.AddNewShows;
                state.LibraryIds = libraries;
                // Not a reason to send anything again: it only filters what comes.
                state.NeverUnwatch = settings.NeverUnwatch;
                if (changed)
                {
                    state.ImportDone = false;
                    state.LibraryFullAt = null;
                    state.PendingLibraryAdded.Clear();
                    state.PendingLibraryRemoved.Clear();
                }
                _store.Save(state);
            }
            Wake();
            return state;
        }

        public IReadOnlyList<MediaLibrary> ListLibraries() => _adapter.ListLibraries();

        private static bool SameLibraries(List<string>? a, List<string>? b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.Count == b.Count && !a.Except(b, StringComparer.OrdinalIgnoreCase).Any();
        }

        private WatchEvent Event(string type, EpisodeRef ep, double? percent) => new WatchEvent
        {
            SeriesName = ep.SeriesName,
            EpisodeName = ep.EpisodeName,
            LocalSeason = ep.Season,
            LocalNumber = ep.Number,
            Type = type,
            TvdbSeriesId = ep.TvdbSeriesId,
            TvdbEpisodeId = ep.TvdbEpisodeId,
            Season = ep.TvdbEpisodeId.HasValue ? null : ep.Season,
            Episode = ep.TvdbEpisodeId.HasValue ? null : ep.Number,
            At = Clock(),
            Percent = percent.HasValue ? Math.Round(percent.Value, 1) : (double?)null,
        };

        private void Remember(LinkState state, WatchEvent e)
        {
            if (e.SeriesName == null) return;
            _labels[state.ServerUserId + "|" + e.EpisodeKey] = new EpisodeRef
            {
                TvdbSeriesId = e.TvdbSeriesId, TvdbEpisodeId = e.TvdbEpisodeId,
                Season = e.LocalSeason ?? e.Season, Number = e.LocalNumber ?? e.Episode,
                SeriesName = e.SeriesName, EpisodeName = e.EpisodeName,
            };
        }

        private ActivityEntry Sent(LinkState state, WatchEvent e)
        {
            _labels.TryRemove(state.ServerUserId + "|" + e.EpisodeKey, out var label);
            LibraryItem? known = null;
            if (label == null)
            {
                try { known = _adapter.Find(e.TvdbSeriesId, e.TvdbEpisodeId, e.Season, e.Episode); }
                catch (Exception) { known = null; }
            }
            return new ActivityEntry
            {
                At = Clock(),
                ToFeedrion = true,
                Action = e.Type == WatchEvent.Unwatched ? ActivityEntry.Unwatched : ActivityEntry.Watched,
                UserName = state.ServerUserName,
                TvdbSeriesId = e.TvdbSeriesId,
                Series = label?.SeriesName ?? known?.SeriesName,
                EpisodeName = label?.EpisodeName ?? known?.EpisodeName,
                Season = label?.Season ?? known?.Season ?? e.Season,
                Episode = label?.Number ?? known?.Number ?? e.Episode,
            };
        }

        private void Enqueue(LinkState state, WatchEvent e)
        {
            Remember(state, e);
            lock (_gate)
            {
                state.Enqueue(e);
                _store.Save(state);
            }
            Wake();
        }

        private bool IsEcho(string serverUserId, string key)
        {
            if (!_echoes.TryGetValue(serverUserId + "|" + key, out var at)) return false;
            return Clock() - at < EchoWindow;
        }

        private void Wake()
        {
            if (_wake.CurrentCount == 0)
            {
                try { _wake.Release(); } catch (SemaphoreFullException) { }
            }
        }

        /// <summary>Wait until something was queued, or [max] passed.</summary>
        public Task<bool> WaitForWorkAsync(TimeSpan max, CancellationToken ct) => _wake.WaitAsync(max, ct);

        // ── The pass ──────────────────────────────────────────────────────────

        public Task RunOnceAsync(CancellationToken ct) => RunPassAsync(waitForRunning: false, ct);

        /// <summary>« Sync now »: a pass right away, awaited, that also sends the
        /// whole library again. A pass already running is waited for rather
        /// than skipped, so the caller sees the result of a pass that started
        /// after the click.</summary>
        public async Task SyncNowAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                foreach (var state in _store.All().Where(s => s.IsLinked))
                {
                    state.LibraryFullAt = null;
                    _store.Save(state);
                }
            }
            await RunPassAsync(waitForRunning: true, ct).ConfigureAwait(false);
        }

        private async Task RunPassAsync(bool waitForRunning, CancellationToken ct)
        {
            if (waitForRunning) await _running.WaitAsync(ct).ConfigureAwait(false);
            else if (!await _running.WaitAsync(0, ct).ConfigureAwait(false)) return;
            try
            {
                foreach (var state in _store.All())
                {
                    if (!state.IsLinked) continue;
                    try
                    {
                        await SyncLinkAsync(state, ct).ConfigureAwait(false);
                        state.LastError = null;
                        state.LastSyncAt = Clock();
                    }
                    catch (FeedrionApiException e) when (e.IsPremiumRequired)
                    {
                        state.Premium = false;
                        state.LastError = "premium_required";
                    }
                    catch (FeedrionApiException e) when (e.IsUnauthorized)
                    {
                        state.LastError = "revoked";
                    }
                    catch (FeedrionApiException e)
                    {
                        state.LastError = e.Detail ?? ("http_" + e.Status);
                        if (!e.IsTransient) _log("Feedrion: sync failed for a linked user", e);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        state.LastError = "error";
                        _log("Feedrion: sync failed for a linked user", e);
                    }
                    lock (_gate) _store.Save(state);
                }
                PruneEchoes();
            }
            finally
            {
                _running.Release();
            }
        }

        private async Task SyncLinkAsync(LinkState state, CancellationToken ct)
        {
            var client = ClientFor(state);
            var me = await client.GetMeAsync(ct).ConfigureAwait(false);
            state.FeedrionName = me.DisplayName;
            state.Premium = me.Premium;
            state.PricingUrl = me.PricingUrl;
            // Premium as a whole: a free account's events wait here, untouched,
            // for the day it is Premium again.
            if (!me.Premium) throw new FeedrionApiException(402, "premium_required");

            var config = await client.GetConfigAsync(ct).ConfigureAwait(false);
            state.ScrobblePercent = config.ScrobblePercent;
            state.FollowLibrary = config.FollowLibrary;
            if (string.IsNullOrEmpty(state.WatchCursor)) state.WatchCursor = config.WatchCursor;

            var info = _adapter.DescribeServer();
            var signature = string.Join("|", info.ServerId, info.ServerName, info.ServerUrl, info.ClientVersion);
            if (state.ServerInfoSent != signature)
            {
                await client.PutServerAsync(info, ct).ConfigureAwait(false);
                state.ServerInfoSent = signature;
            }

            if (!state.ImportDone)
            {
                // The history the server already holds, once per link; the
                // events played since come after it in the queue.
                var history = _adapter.ListPlayedEpisodes(state.ServerUserId)
                    .Where(e => state.Covers(e.LibraryIds) && !IsIgnored(e.TvdbSeriesId)).ToList();
                lock (_gate)
                {
                    var later = state.PendingEvents.ToList();
                    state.PendingEvents.Clear();
                    foreach (var e in history)
                    {
                        Remember(state, e);
                        state.Enqueue(e);
                    }
                    foreach (var e in later) state.Enqueue(e);
                    state.ImportDone = true;
                    _store.Save(state);
                }
            }

            await FlushEventsAsync(client, state, ct).ConfigureAwait(false);
            await SyncLibraryAsync(client, state, ct).ConfigureAwait(false);
            await PullWatchesAsync(client, state, ct).ConfigureAwait(false);
        }

        private async Task FlushEventsAsync(FeedrionClient client, LinkState state, CancellationToken ct)
        {
            while (true)
            {
                List<WatchEvent> batch;
                var ignored = IgnoredSeries();
                lock (_gate)
                {
                    // Queued before its series was ignored, or before « never
                    // unwatch » was ticked: never sent.
                    var dropped = state.PendingEvents.RemoveAll(e =>
                        (ignored.Count > 0 && ignored.Contains(e.TvdbSeriesId))
                        || (state.NeverUnwatch && e.Type == WatchEvent.Unwatched));
                    if (dropped > 0) _store.Save(state);
                    batch = state.PendingEvents.Take(EventsPerCall).ToList();
                }
                if (batch.Count == 0) return;
                await client.PostEventsAsync(batch, state.AddNewShows, ct).ConfigureAwait(false);
                // Every label of the batch is released; the log keeps the last 50.
                var logged = batch.Select(e => Sent(state, e)).ToList();
                _activity.Add(logged.Skip(Math.Max(0, logged.Count - ActivityLog.Keep)));
                lock (_gate)
                {
                    // Only what was sent, and only if nothing newer replaced it.
                    foreach (var sent in batch) state.PendingEvents.Remove(sent);
                    _store.Save(state);
                }
            }
        }

        private async Task SyncLibraryAsync(FeedrionClient client, LinkState state, CancellationToken ct)
        {
            var due = state.LibraryFullAt == null || Clock() - state.LibraryFullAt.Value > LibraryResendEvery;
            if (due)
            {
                var items = _adapter.ListLibraryEpisodes()
                    .Where(i => state.Covers(i.LibraryIds) && !IsIgnored(i.TvdbSeriesId)).ToList();
                var generation = Clock().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var pages = items.Count == 0 ? 1 : (items.Count + LibraryPerCall - 1) / LibraryPerCall;
                for (var page = 0; page < pages; page++)
                {
                    await client.PostLibraryAsync(new LibraryBatch
                    {
                        Items = items.Skip(page * LibraryPerCall).Take(LibraryPerCall).ToList(),
                        Generation = generation,
                        Final = page == pages - 1,
                    }, ct).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    state.LibraryFullAt = Clock();
                    state.PendingLibraryAdded.Clear();
                    state.PendingLibraryRemoved.Clear();
                    _store.Save(state);
                }
                return;
            }
            while (true)
            {
                List<LibraryItem> added;
                List<int> removed;
                lock (_gate)
                {
                    state.PendingLibraryAdded.RemoveAll(a => IsIgnored(a.TvdbSeriesId));
                    added = state.PendingLibraryAdded.Take(LibraryPerCall).ToList();
                    removed = state.PendingLibraryRemoved.Take(LibraryPerCall).ToList();
                }
                if (added.Count == 0 && removed.Count == 0) return;
                await client.PostLibraryAsync(new LibraryBatch { Items = added, Removed = removed }, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var a in added) state.PendingLibraryAdded.Remove(a);
                    foreach (var r in removed) state.PendingLibraryRemoved.Remove(r);
                    _store.Save(state);
                }
            }
        }

        private async Task PullWatchesAsync(FeedrionClient client, LinkState state, CancellationToken ct)
        {
            for (var page = 0; page < WatchPagesPerPass; page++)
            {
                var watches = await client.GetWatchedAsync(state.WatchCursor, WatchesPerCall, ct).ConfigureAwait(false);
                var applied = new List<ActivityEntry>();
                foreach (var watch in watches.Watches)
                {
                    if (IsIgnored(watch.TvdbSeriesId)) continue;
                    var key = "e" + watch.TvdbEpisodeId;
                    // Before the server is touched: its own event comes back
                    // from inside MarkPlayed on some servers.
                    _echoes[state.ServerUserId + "|" + key] = Clock();
                    _echoes[state.ServerUserId + "|s" + watch.TvdbSeriesId + ":" + watch.Season + ":" + watch.Episode] = Clock();
                    try
                    {
                        var marked = _adapter.MarkPlayed(state.ServerUserId, watch, state.Covers);
                        if (marked != null) applied.Add(new ActivityEntry
                        {
                            At = Clock(),
                            ToFeedrion = false,
                            Action = ActivityEntry.Watched,
                            UserName = state.ServerUserName,
                            TvdbSeriesId = watch.TvdbSeriesId,
                            Series = marked.SeriesName,
                            EpisodeName = marked.EpisodeName,
                            Season = watch.Season,
                            Episode = watch.Episode,
                        });
                    }
                    catch (Exception e)
                    {
                        _log("Feedrion: could not mark an episode played", e);
                    }
                }
                _activity.Add(applied);
                lock (_gate)
                {
                    state.WatchCursor = watches.Cursor;
                    _store.Save(state);
                }
                if (!watches.More) return;
            }
        }

        private void PruneEchoes()
        {
            var now = Clock();
            foreach (var entry in _echoes)
                if (now - entry.Value > EchoWindow) _echoes.TryRemove(entry.Key, out _);
        }
    }
}
