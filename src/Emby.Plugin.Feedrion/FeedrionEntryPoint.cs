using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.Feedrion
{
    /// <summary>Listens to Emby and runs the sync loop — the twin of the Jellyfin
    /// plugin's hosted service.</summary>
    public sealed class FeedrionEntryPoint : IServerEntryPoint
    {
        private static readonly TimeSpan LibraryFlushEvery = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

        private readonly ISessionManager _sessions;
        private readonly IUserDataManager _userData;
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, LibraryItem> _added = new ConcurrentDictionary<int, LibraryItem>();
        private readonly ConcurrentDictionary<int, byte> _removed = new ConcurrentDictionary<int, byte>();
        private readonly HttpClient _http;

        public FeedrionEntryPoint(
            ISessionManager sessions, IUserDataManager userData, ILibraryManager library,
            IUserManager users, IServerApplicationHost host, ILogManager logs)
        {
            _sessions = sessions;
            _userData = userData;
            _library = library;
            _logger = logs.GetLogger("Feedrion");
            Users = users;
            Host = host;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Feedrion-Emby/" + typeof(Plugin).Assembly.GetName().Version?.ToString(3));
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Feedrion plugin not loaded.");
            Adapter = new EmbyAdapter(library, users, userData, host);
            Engine = new SyncEngine(
                Adapter,
                new LinkStore(Path.Combine(plugin.DataFolderPath, "links")),
                _http,
                () => plugin.Configuration.ApiBaseUrl,
                (message, error) => _logger.ErrorException(message, error ?? new Exception(message)));
            Instance = this;
        }

        /// <summary>The running instance, for the REST service (Emby resolves
        /// services and entry points separately).</summary>
        public static FeedrionEntryPoint? Instance { get; private set; }

        public SyncEngine Engine { get; }
        public EmbyAdapter Adapter { get; }
        public IUserManager Users { get; }
        public IServerApplicationHost Host { get; }

        public void Run()
        {
            _sessions.PlaybackStopped += OnPlaybackStopped;
            _userData.UserDataSaved += OnUserDataSaved;
            _library.ItemAdded += OnItemAdded;
            _library.ItemRemoved += OnItemRemoved;
            _ = Task.Run(() => LoopAsync(_stop.Token));
        }

        public void Dispose()
        {
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _userData.UserDataSaved -= OnUserDataSaved;
            _library.ItemAdded -= OnItemAdded;
            _library.ItemRemoved -= OnItemRemoved;
            _stop.Cancel();
            _http.Dispose();
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                var lastLibraryFlush = DateTimeOffset.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        FlushLibrary();
                        lastLibraryFlush = DateTimeOffset.UtcNow;
                        await Engine.RunOnceAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        _logger.ErrorException("Feedrion: sync pass failed", e);
                    }
                    var interval = TimeSpan.FromMinutes(Math.Max(5, Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 15));
                    var woken = await Engine.WaitForWorkAsync(interval, ct).ConfigureAwait(false);
                    if (woken) await Task.Delay(Debounce, ct).ConfigureAwait(false);
                    if (!woken && DateTimeOffset.UtcNow - lastLibraryFlush < LibraryFlushEvery)
                        await Task.Delay(LibraryFlushEvery, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Server shutting down.
            }
        }

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            try
            {
                if (!(e.Item is Episode episode)) return;
                var reference = EmbyAdapter.ToRef(episode);
                if (reference == null) return;
                var percent = Scrobble.PlayedPercent(e.PlaybackPositionTicks, episode.RunTimeTicks);
                foreach (var user in e.Users)
                    Engine.RecordPlayback(EmbyAdapter.UserKey(user), reference, percent, e.PlayedToCompletion);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Feedrion: could not record a playback", ex);
            }
        }

        private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            if (e.SaveReason != UserDataSaveReason.TogglePlayed || !(e.Item is Episode episode) || e.User == null) return;
            try
            {
                var reference = EmbyAdapter.ToRef(episode);
                if (reference == null || e.UserData == null) return;
                Engine.RecordPlayedToggle(EmbyAdapter.UserKey(e.User), reference, e.UserData.Played);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Feedrion: could not record a played mark", ex);
            }
        }

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            var item = EmbyAdapter.ToLibraryItem(e.Item);
            if (item == null) return;
            _removed.TryRemove(item.TvdbEpisodeId, out _);
            _added[item.TvdbEpisodeId] = item;
        }

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            var reference = EmbyAdapter.ToRef(e.Item);
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
            Engine.RecordLibraryChange(added, removed);
        }
    }
}
