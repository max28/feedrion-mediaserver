using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Feedrion
{
    /// <summary>Listens to Jellyfin and runs the sync loop.</summary>
    public sealed class FeedrionSyncService : BackgroundService
    {
        // A library scan raises one event per episode: they are gathered and
        // handed to the engine in one go, not saved one by one.
        private static readonly TimeSpan LibraryFlushEvery = TimeSpan.FromSeconds(30);
        // A playback that just stopped is sent within this, not at the next pass.
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

        private readonly SyncEngine _engine;
        private readonly JellyfinAdapter _adapter;
        private readonly ISessionManager _sessions;
        private readonly IUserDataManager _userData;
        private readonly ILibraryManager _library;
        private readonly ILogger<FeedrionSyncService> _logger;
        private readonly ConcurrentDictionary<int, LibraryItem> _added = new ConcurrentDictionary<int, LibraryItem>();
        private readonly ConcurrentDictionary<int, byte> _removed = new ConcurrentDictionary<int, byte>();

        public FeedrionSyncService(
            SyncEngine engine, JellyfinAdapter adapter, ISessionManager sessions, IUserDataManager userData,
            ILibraryManager library, ILogger<FeedrionSyncService> logger)
        {
            _engine = engine;
            _adapter = adapter;
            _sessions = sessions;
            _userData = userData;
            _library = library;
            _logger = logger;
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStopped += OnPlaybackStopped;
            _userData.UserDataSaved += OnUserDataSaved;
            _library.ItemAdded += OnItemAdded;
            _library.ItemRemoved += OnItemRemoved;
            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _userData.UserDataSaved -= OnUserDataSaved;
            _library.ItemAdded -= OnItemAdded;
            _library.ItemRemoved -= OnItemRemoved;
            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the server finish starting before the first pass.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            var lastLibraryFlush = DateTimeOffset.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    FlushLibrary();
                    lastLibraryFlush = DateTimeOffset.UtcNow;
                    await _engine.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Feedrion: sync pass failed");
                }
                var interval = TimeSpan.FromMinutes(Math.Max(5, Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 15));
                var woken = await _engine.WaitForWorkAsync(interval, stoppingToken).ConfigureAwait(false);
                if (woken) await Task.Delay(Debounce, stoppingToken).ConfigureAwait(false);
                if (!woken && DateTimeOffset.UtcNow - lastLibraryFlush < LibraryFlushEvery)
                    await Task.Delay(LibraryFlushEvery, stoppingToken).ConfigureAwait(false);
            }
        }

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            try
            {
                if (e.Item is not Episode episode) return;
                var reference = _adapter.Reference(episode);
                if (reference == null) return;
                var percent = Scrobble.PlayedPercent(e.PlaybackPositionTicks, episode.RunTimeTicks);
                foreach (var user in e.Users)
                    _engine.RecordPlayback(JellyfinAdapter.UserKey(user.Id), reference, percent, e.PlayedToCompletion);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feedrion: could not record a playback");
            }
        }

        private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            // Only the reader's own « mark played / unplayed ». A finished
            // playback arrives through PlaybackStopped, with its percent.
            if (e.SaveReason != UserDataSaveReason.TogglePlayed || e.Item is not Episode episode) return;
            try
            {
                var reference = _adapter.Reference(episode);
                if (reference == null || e.UserData == null) return;
                _engine.RecordPlayedToggle(JellyfinAdapter.UserKey(e.UserId), reference, e.UserData.Played);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feedrion: could not record a played mark");
            }
        }

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            LibraryItem? item;
            try
            {
                item = _adapter.Item(e.Item);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feedrion: could not read an added episode");
                return;
            }
            if (item == null) return;
            _removed.TryRemove(item.TvdbEpisodeId, out _);
            _added[item.TvdbEpisodeId] = item;
        }

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            var reference = JellyfinAdapter.ToRef(e.Item);
            if (reference?.TvdbEpisodeId == null) return;
            _added.TryRemove(reference.TvdbEpisodeId.Value, out _);
            _removed[reference.TvdbEpisodeId.Value] = 0;
        }

        private void FlushLibrary()
        {
            var added = new List<LibraryItem>();
            foreach (var key in _added.Keys)
                if (_added.TryRemove(key, out var item)) added.Add(item);
            var removed = new List<int>();
            foreach (var key in _removed.Keys)
                if (_removed.TryRemove(key, out _)) removed.Add(key);
            _engine.RecordLibraryChange(added, removed);
        }
    }
}
