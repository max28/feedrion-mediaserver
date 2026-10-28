using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Feedrion.MediaServer.Core.Tests
{
    public sealed class CoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "feedrion-tests-" + Guid.NewGuid().ToString("N"));
        private readonly FakeFeedrion _api = new FakeFeedrion();
        private readonly FakeAdapter _adapter = new FakeAdapter();

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private const string UserId = "0f8fad5bd9cb469fa16570867728950e";

        private (SyncEngine Engine, LinkStore Store) Engine(bool linked = true)
        {
            var store = new LinkStore(_dir);
            if (linked)
                store.Save(new LinkState
                {
                    ServerUserId = UserId, AccessToken = _api.ValidAccess, RefreshToken = _api.ValidRefresh,
                });
            var engine = new SyncEngine(_adapter, store, new HttpClient(_api), () => "https://feedrion.example/api",
                (m, e) => { });
            return (engine, store);
        }

        private static EpisodeRef Ep(int id) => new EpisodeRef { TvdbSeriesId = 500, TvdbEpisodeId = id, Season = 1, Number = id % 100 };

        [Theory]
        [InlineData(89.9, false, 90, false)]
        [InlineData(90.0, false, 90, true)]
        [InlineData(10.0, true, 90, true)]
        [InlineData(null, false, 90, false)]
        [InlineData(60.0, false, 50, true)]
        [InlineData(80.0, false, 20, false)] // an out-of-range threshold falls back to 90
        public void A_playback_counts_at_the_reader_s_threshold(double? percent, bool completed, int threshold, bool expected)
        {
            Assert.Equal(expected, Scrobble.CountsAsWatched(percent, completed, threshold));
        }

        [Fact]
        public void The_played_percent_needs_a_runtime()
        {
            Assert.Null(Scrobble.PlayedPercent(100, null));
            Assert.Null(Scrobble.PlayedPercent(100, 0));
            Assert.Equal(50, Scrobble.PlayedPercent(50, 100));
            Assert.Equal(100, Scrobble.PlayedPercent(150, 100));
        }

        [Fact]
        public void The_api_address_must_be_https()
        {
            Assert.Equal(FeedrionClient.DefaultBaseUrl, FeedrionClient.NormalizeBaseUrl(" "));
            Assert.Equal("https://x.example/api", FeedrionClient.NormalizeBaseUrl("https://x.example/api/"));
            Assert.Throws<ArgumentException>(() => FeedrionClient.NormalizeBaseUrl("http://x.example/api"));
            Assert.Throws<ArgumentException>(() => FeedrionClient.NormalizeBaseUrl("not a url"));
        }

        [Fact]
        public void The_last_event_about_an_episode_wins_and_the_queue_is_bounded()
        {
            var state = new LinkState();
            state.Enqueue(new WatchEvent { TvdbSeriesId = 1, TvdbEpisodeId = 11, Type = WatchEvent.Watched });
            state.Enqueue(new WatchEvent { TvdbSeriesId = 1, TvdbEpisodeId = 11, Type = WatchEvent.Unwatched });
            Assert.Single(state.PendingEvents);
            Assert.Equal(WatchEvent.Unwatched, state.PendingEvents[0].Type);
            for (var i = 0; i < LinkState.MaxPendingEvents + 10; i++)
                state.Enqueue(new WatchEvent { TvdbSeriesId = 2, TvdbEpisodeId = 1000 + i });
            Assert.Equal(LinkState.MaxPendingEvents, state.PendingEvents.Count);
        }

        [Fact]
        public void Links_survive_a_restart_and_a_damaged_file_is_skipped()
        {
            var (_, store) = Engine();
            File.WriteAllText(Path.Combine(_dir, "link-broken.json"), "{not json");
            var reloaded = new LinkStore(_dir);
            Assert.Single(reloaded.All());
            Assert.Equal(_api.ValidRefresh, reloaded.Get(UserId)!.RefreshToken);
        }

        [Fact]
        public async Task An_expired_access_token_is_rotated_once_and_saved()
        {
            var (engine, store) = Engine();
            _api.ValidAccess = "access-expired-on-server"; // the stored one no longer works
            await engine.RunOnceAsync(CancellationToken.None);
            var state = store.Get(UserId)!;
            Assert.Equal("access-2", state.AccessToken);
            Assert.Equal("refresh-2", state.RefreshToken);
            Assert.Null(state.LastError);
        }

        [Fact]
        public async Task A_session_revoked_in_the_app_is_forgotten_here()
        {
            var (engine, store) = Engine();
            _api.ValidAccess = "other";
            _api.RefreshRevoked = true;
            await engine.RunOnceAsync(CancellationToken.None);
            var state = store.Get(UserId)!;
            Assert.False(state.IsLinked);
            Assert.Equal("revoked", state.LastError);
        }

        [Fact]
        public async Task A_free_account_keeps_its_events_and_sends_none()
        {
            var (engine, store) = Engine();
            _api.Premium = false;
            engine.RecordPlayedToggle(UserId, Ep(51), played: true);
            await engine.RunOnceAsync(CancellationToken.None);
            var state = store.Get(UserId)!;
            Assert.Equal("premium_required", state.LastError);
            Assert.Single(state.PendingEvents);
            Assert.Empty(_api.EventBatches);
            Assert.DoesNotContain(_api.Calls, c => c.Path.Contains("/library"));
        }

        [Fact]
        public async Task A_pass_imports_the_history_first_then_sends_the_library_by_generation()
        {
            var (engine, store) = Engine();
            _adapter.History.Add(new WatchEvent { TvdbSeriesId = 500, TvdbEpisodeId = 501, Type = WatchEvent.Watched });
            _adapter.Library.Add(new LibraryItem { TvdbSeriesId = 500, TvdbEpisodeId = 501, ItemId = "a" });
            engine.RecordPlayedToggle(UserId, Ep(502), played: true);
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Null(store.Get(UserId)!.LastError);

            var sent = _api.EventBatches.Single();
            Assert.Equal(new int?[] { 501, 502 }, sent.Select(e => e.TvdbEpisodeId).ToArray());
            var library = _api.LibraryBatches.Single();
            Assert.True(library.Final);
            Assert.NotNull(library.Generation);
            var state = store.Get(UserId)!;
            Assert.True(state.ImportDone);
            Assert.Empty(state.PendingEvents);

            // The import happens once.
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Single(_api.EventBatches);
        }

        [Fact]
        public async Task A_playback_below_the_threshold_is_not_queued()
        {
            var (engine, store) = Engine();
            engine.RecordPlayback(UserId, Ep(601), 40, playedToCompletion: false);
            engine.RecordPlayback(UserId, Ep(602), 95, playedToCompletion: false);
            var pending = store.Get(UserId)!.PendingEvents;
            Assert.Equal(new int?[] { 602 }, pending.Select(e => e.TvdbEpisodeId).ToArray());
            Assert.Equal(95, pending[0].Percent);
        }

        [Fact]
        public async Task A_watch_from_elsewhere_is_marked_here_and_its_echo_is_not_sent_back()
        {
            var (engine, store) = Engine();
            _api.Watches.Add(new RemoteWatch
            {
                TvdbSeriesId = 500, TvdbEpisodeId = 701, Season = 1, Episode = 1, WatchedAt = DateTimeOffset.UtcNow,
            });
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Single(_adapter.Marked);
            Assert.Equal("next|1", store.Get(UserId)!.WatchCursor);
            // The server reports the mark it was just asked to make.
            engine.RecordPlayedToggle(UserId, Ep(701), played: true);
            Assert.Empty(store.Get(UserId)!.PendingEvents);
        }

        [Fact]
        public async Task Pairing_waits_for_the_reader_then_stores_a_session()
        {
            var (engine, store) = Engine(linked: false);
            var pending = await engine.StartLinkAsync(UserId, "max", "Jellyfin · Salon", CancellationToken.None);
            Assert.Equal("ABCD-EFGH", pending.UserCode);
            Assert.NotNull(engine.GetPending(UserId));
            _api.PairStatus = "approved";
            for (var i = 0; i < 50 && store.Get(UserId)?.IsLinked != true; i++) await Task.Delay(100);
            var state = store.Get(UserId)!;
            Assert.True(state.IsLinked);
            Assert.Equal("max", state.ServerUserName);
            Assert.Null(engine.GetPending(UserId));
            var start = _api.Calls.First(c => c.Path.Contains("pair/start"));
            Assert.Contains("\"kind\":\"jellyfin\"", start.Body);
        }

        [Fact]
        public async Task Unlinking_signs_out_on_both_sides()
        {
            var (engine, store) = Engine();
            await engine.UnlinkAsync(UserId, CancellationToken.None);
            Assert.Null(store.Get(UserId));
            Assert.Contains(_api.Calls, c => c.Path.Contains("/disconnect"));
            Assert.False(File.Exists(Path.Combine(_dir, "link-" + UserId + ".json")));
        }

        [Fact]
        public void A_library_change_reaches_every_link_and_an_overflow_falls_back_to_a_full_resend()
        {
            var (engine, store) = Engine();
            engine.RecordLibraryChange(new[] { new LibraryItem { TvdbSeriesId = 1, TvdbEpisodeId = 9, ItemId = "x" } }, new int[0]);
            Assert.Single(store.Get(UserId)!.PendingLibraryAdded);
            var state = store.Get(UserId)!;
            state.LibraryFullAt = DateTimeOffset.UtcNow;
            state.EnqueueLibrary(Enumerable.Range(0, LinkState.MaxPendingLibrary + 1)
                .Select(i => new LibraryItem { TvdbSeriesId = 1, TvdbEpisodeId = 10 + i, ItemId = "i" + i }), new int[0]);
            Assert.Empty(state.PendingLibraryAdded);
            Assert.Null(state.LibraryFullAt);
        }

        [Fact]
        public async Task Sync_now_runs_a_pass_at_once_and_sends_the_whole_library_again()
        {
            var (engine, store) = Engine();
            _adapter.Library.Add(new LibraryItem { TvdbSeriesId = 500, TvdbEpisodeId = 501, ItemId = "a" });
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Single(_api.LibraryBatches);

            // A periodic pass within the day sends nothing new…
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Single(_api.LibraryBatches);

            // …« Sync now » does, and has finished when it returns.
            store.Get(UserId)!.LastSyncAt = null;
            await engine.SyncNowAsync(CancellationToken.None);
            Assert.Equal(2, _api.LibraryBatches.Count);
            Assert.True(_api.LibraryBatches[1].Final);
            Assert.NotNull(store.Get(UserId)!.LastSyncAt);
            Assert.Null(store.Get(UserId)!.LastError);
        }
    
        [Fact]
        public async Task A_link_does_not_add_new_series_unless_told_to()
        {
            var (engine, store) = Engine();
            engine.RecordPlayedToggle(UserId, Ep(801), played: true);
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.False(store.Get(UserId)!.AddNewShows);
            Assert.Equal(new bool?[] { false }, _api.EventBatchAddNewShows.ToArray());

            engine.UpdateSettings(UserId, "max", new LinkSettings { AddNewShows = true });
            engine.RecordPlayedToggle(UserId, Ep(802), played: true);
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Equal(true, _api.EventBatchAddNewShows.Last());
        }

        private static EpisodeRef EpIn(int id, string library) => new EpisodeRef
        {
            TvdbSeriesId = 500, TvdbEpisodeId = id, Season = 1, Number = id % 100, LibraryIds = new[] { library },
        };

        [Fact]
        public async Task A_link_only_sends_what_its_libraries_hold()
        {
            var (engine, store) = Engine();
            var state = engine.UpdateSettings(UserId, "max", new LinkSettings
            {
                LibraryIds = new System.Collections.Generic.List<string> { "LIB-SHOWS", "gone" },
            });
            // Normalised, and a library the server does not have is dropped.
            Assert.Equal(new[] { "lib-shows" }, state.LibraryIds!.ToArray());

            _adapter.History.Add(new WatchEvent { TvdbSeriesId = 500, TvdbEpisodeId = 901, LibraryIds = new[] { "lib-shows" } });
            _adapter.History.Add(new WatchEvent { TvdbSeriesId = 600, TvdbEpisodeId = 902, LibraryIds = new[] { "lib-kids" } });
            _adapter.History.Add(new WatchEvent { TvdbSeriesId = 700, TvdbEpisodeId = 903 });
            _adapter.Library.Add(new LibraryItem { TvdbSeriesId = 500, TvdbEpisodeId = 901, ItemId = "a", LibraryIds = new[] { "lib-shows" } });
            _adapter.Library.Add(new LibraryItem { TvdbSeriesId = 600, TvdbEpisodeId = 902, ItemId = "b", LibraryIds = new[] { "lib-kids" } });
            engine.RecordPlayback(UserId, EpIn(904, "lib-kids"), 100, playedToCompletion: true);
            engine.RecordPlayedToggle(UserId, EpIn(905, "lib-shows"), played: true);
            Assert.Equal(new int?[] { 905 }, store.Get(UserId)!.PendingEvents.Select(e => e.TvdbEpisodeId).ToArray());

            _adapter.SeriesLibrary[500] = "lib-shows";
            _adapter.SeriesLibrary[600] = "lib-kids";
            _api.Watches.Add(new RemoteWatch { TvdbSeriesId = 600, TvdbEpisodeId = 906, Season = 1, Episode = 6, WatchedAt = DateTimeOffset.UtcNow });
            _api.Watches.Add(new RemoteWatch { TvdbSeriesId = 500, TvdbEpisodeId = 907, Season = 1, Episode = 7, WatchedAt = DateTimeOffset.UtcNow });
            await engine.RunOnceAsync(CancellationToken.None);

            Assert.Equal(new int?[] { 901, 905 }, _api.EventBatches.Single().Select(e => e.TvdbEpisodeId).ToArray());
            Assert.Equal(new[] { 901 }, _api.LibraryBatches.Single().Items.Select(i => i.TvdbEpisodeId).ToArray());
            Assert.Equal(new[] { 907 }, _adapter.Marked.Select(w => w.TvdbEpisodeId).ToArray());

            engine.RecordLibraryChange(new[]
            {
                new LibraryItem { TvdbSeriesId = 600, TvdbEpisodeId = 908, ItemId = "c", LibraryIds = new[] { "lib-kids" } },
            }, new int[0]);
            Assert.Empty(store.Get(UserId)!.PendingLibraryAdded);
        }

        [Fact]
        public async Task Changing_the_settings_sends_the_inventory_and_the_history_again()
        {
            var (engine, store) = Engine();
            _adapter.History.Add(new WatchEvent { TvdbSeriesId = 500, TvdbEpisodeId = 911, LibraryIds = new[] { "lib-kids" } });
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Single(_api.EventBatches);
            Assert.Single(_api.LibraryBatches);

            // Same settings: nothing to redo.
            engine.UpdateSettings(UserId, "max", new LinkSettings());
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Single(_api.EventBatches);

            engine.UpdateSettings(UserId, "max", new LinkSettings { AddNewShows = true });
            Assert.False(store.Get(UserId)!.ImportDone);
            Assert.Null(store.Get(UserId)!.LibraryFullAt);
            await engine.RunOnceAsync(CancellationToken.None);
            Assert.Equal(2, _api.EventBatches.Count);
            Assert.Equal(2, _api.LibraryBatches.Count);
        }

        [Fact]
        public void Settings_set_before_linking_survive_the_pairing()
        {
            var (engine, store) = Engine(linked: false);
            engine.UpdateSettings(UserId, "kid", new LinkSettings { LibraryIds = new System.Collections.Generic.List<string>() });
            var state = store.Get(UserId)!;
            Assert.False(state.IsLinked);
            Assert.Empty(state.LibraryIds!);
            Assert.False(state.Covers(new[] { "lib-shows" }));
            Assert.True(new LinkState().Covers(null));
            Assert.False(new LinkState { LibraryIds = new System.Collections.Generic.List<string> { "lib-shows" } }.Covers(null));
            // A file written before these settings existed reads as « all, no new series ».
            File.WriteAllText(Path.Combine(_dir, "link-" + UserId + ".json"),
                "{\"server_user_id\":\"" + UserId + "\",\"refresh_token\":\"r\"}");
            var reread = new LinkStore(_dir).Get(UserId)!;
            Assert.Null(reread.LibraryIds);
            Assert.False(reread.AddNewShows);
        }
    
        [Fact]
        public async Task An_ignored_series_never_reaches_feedrion()
        {
            var (engine, store) = Engine();
            var ignored = new System.Collections.Generic.List<int>();
            engine.IgnoredSeries = () => ignored;
            // Queued before the series was ignored.
            engine.RecordPlayedToggle(UserId, new EpisodeRef { TvdbSeriesId = 600, TvdbEpisodeId = 1001 }, played: true);
            ignored.Add(600);
            engine.RecordPlayback(UserId, new EpisodeRef { TvdbSeriesId = 600, TvdbEpisodeId = 1002 }, 100, true);
            engine.RecordPlayedToggle(UserId, Ep(1003), played: true);
            _adapter.History.Add(new WatchEvent { TvdbSeriesId = 600, TvdbEpisodeId = 1004 });
            _adapter.Library.Add(new LibraryItem { TvdbSeriesId = 600, TvdbEpisodeId = 1005, ItemId = "k" });
            _adapter.Library.Add(new LibraryItem { TvdbSeriesId = 500, TvdbEpisodeId = 1006, ItemId = "n" });
            _api.Watches.Add(new RemoteWatch { TvdbSeriesId = 600, TvdbEpisodeId = 1007, Season = 1, Episode = 7, WatchedAt = DateTimeOffset.UtcNow });
            await engine.RunOnceAsync(CancellationToken.None);

            Assert.Equal(new int?[] { 1003 }, _api.EventBatches.Single().Select(e => e.TvdbEpisodeId).ToArray());
            Assert.Equal(new[] { 1006 }, _api.LibraryBatches.Single().Items.Select(i => i.TvdbEpisodeId).ToArray());
            Assert.Empty(_adapter.Marked);
            Assert.Empty(store.Get(UserId)!.PendingEvents);

            engine.RecordLibraryChange(new[] { new LibraryItem { TvdbSeriesId = 600, TvdbEpisodeId = 1008, ItemId = "z" } }, new int[0]);
            Assert.Empty(store.Get(UserId)!.PendingLibraryAdded);

            // Changing the list sends the inventory again.
            engine.IgnoredSeriesChanged();
            Assert.Null(store.Get(UserId)!.LibraryFullAt);
        }
    
        [Fact]
        public async Task The_activity_log_keeps_the_last_fifty_changes_both_ways_with_local_names()
        {
            var (engine, store) = Engine();
            var state = store.Get(UserId)!;
            state.ServerUserName = "max";
            store.Save(state);
            engine.RecordPlayedToggle(UserId, new EpisodeRef
            {
                TvdbSeriesId = 500, TvdbEpisodeId = 1201, Season = 2, Number = 3,
                SeriesName = "Northwind", EpisodeName = "The Harbour",
            }, played: true);
            _api.Watches.Add(new RemoteWatch { TvdbSeriesId = 600, TvdbEpisodeId = 1202, Season = 1, Episode = 4, WatchedAt = DateTimeOffset.UtcNow });
            await engine.RunOnceAsync(CancellationToken.None);

            var log = engine.Activity();
            Assert.Equal(2, log.Count);
            Assert.False(log[0].ToFeedrion);
            Assert.Equal("Series 600", log[0].Series);
            Assert.True(log[1].ToFeedrion);
            Assert.Equal("Northwind", log[1].Series);
            Assert.Equal("The Harbour", log[1].EpisodeName);
            Assert.Equal(2, log[1].Season);
            Assert.Equal(3, log[1].Episode);
            Assert.Equal("max", log[1].UserName);

            // Names stay on the server: nothing of them was sent.
            Assert.DoesNotContain(_api.Calls, c => c.Body != null && c.Body.Contains("Northwind"));

            // Fifty at most, and it survives a restart.
            for (var i = 0; i < 60; i++) engine.RecordPlayedToggle(UserId, Ep(1300 + i), played: true);
            await engine.RunOnceAsync(CancellationToken.None);
            var reopened = new SyncEngine(_adapter, new LinkStore(_dir), new HttpClient(_api), () => "https://feedrion.example/api", (m, e) => { });
            Assert.Equal(ActivityLog.Keep, reopened.Activity().Count);
            Assert.True(reopened.Activity()[0].ToFeedrion);
        }

        [Fact]
        public void A_new_link_never_reports_an_episode_unwatched_by_default()
        {
            Assert.True(new LinkState().NeverUnwatch);
            Assert.True(new LinkSettings().NeverUnwatch);
            Assert.True(LinkView.From("u", "max", null, null).NeverUnwatch);
        }

        [Fact]
        public async Task A_link_can_refuse_to_ever_report_an_episode_unwatched()
        {
            var (engine, store) = Engine();
            // Queued while the option was unticked.
            engine.UpdateSettings(UserId, "max", new LinkSettings { NeverUnwatch = false });
            engine.RecordPlayedToggle(UserId, Ep(1401), played: false);
            Assert.Contains(store.Get(UserId)!.PendingEvents, e => e.Type == WatchEvent.Unwatched);
            engine.UpdateSettings(UserId, "max", new LinkSettings { NeverUnwatch = true });
            Assert.True(store.Get(UserId)!.NeverUnwatch);
            engine.RecordPlayedToggle(UserId, Ep(1402), played: false);
            engine.RecordPlayedToggle(UserId, Ep(1403), played: true);
            await engine.RunOnceAsync(CancellationToken.None);
            var sent = _api.EventBatches.SelectMany(b => b).ToList();
            Assert.DoesNotContain(sent, e => e.Type == WatchEvent.Unwatched);
            Assert.Contains(sent, e => e.TvdbEpisodeId == 1403);
        }
    }
}
