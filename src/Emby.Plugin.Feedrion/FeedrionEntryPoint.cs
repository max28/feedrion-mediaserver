using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
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

        private static readonly object StartGate = new object();

        private readonly ISessionManager _sessions;
        private readonly IUserDataManager _userData;
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, LibraryItem> _added = new ConcurrentDictionary<int, LibraryItem>();
        private readonly ConcurrentDictionary<int, byte> _removed = new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentDictionary<string, MovieLibraryItem> _addedMovies = new ConcurrentDictionary<string, MovieLibraryItem>();
        private readonly ConcurrentDictionary<string, byte> _removedMovies = new ConcurrentDictionary<string, byte>();
        private HttpClient? _http;
        private bool _hooked;

        // Takes its dependencies and nothing else: a constructor that throws is
        // dropped by Emby without a word, and the plugin then never starts.
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
        }

        /// <summary>The running instance, for the REST service (Emby resolves
        /// services and entry points separately).</summary>
        public static FeedrionEntryPoint? Instance { get; private set; }

        /// <summary>Why the last start failed, for the configuration page.</summary>
        public static string? StartupError { get; private set; }

        public SyncEngine Engine { get; private set; } = null!;
        public EmbyAdapter Adapter { get; private set; } = null!;
        public IUserManager Users { get; }
        public IServerApplicationHost Host { get; }

        /// <summary>The running instance, started here if Emby never ran its
        /// entry point — the plugin must not depend on that alone (seen on
        /// Emby 4.11: the page answered, the engine did not exist).</summary>
        public static FeedrionEntryPoint Ensure(
            ISessionManager sessions, IUserDataManager userData, ILibraryManager library,
            IUserManager users, IServerApplicationHost host, ILogManager logs)
        {
            var running = Instance;
            if (running != null) return running;
            new FeedrionEntryPoint(sessions, userData, library, users, host, logs).Run();
            return Instance ?? throw new InvalidOperationException(
                "Feedrion could not start: " + (StartupError ?? "unknown error") + ".");
        }

        public void Run()
        {
            lock (StartGate)
            {
                if (Instance != null) return;
                try
                {
                    var plugin = Plugin.Instance ?? throw new InvalidOperationException("Feedrion plugin not loaded.");
                    _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Feedrion-Emby/" + typeof(Plugin).Assembly.GetName().Version?.ToString(3));
                    Adapter = new EmbyAdapter(_library, Users, _userData, Host);
                    Engine = new SyncEngine(
                        Adapter,
                        new LinkStore(Path.Combine(plugin.DataFolderPath, "links")),
                        _http,
                        () => plugin.Configuration.ApiBaseUrl,
                        (message, error) => _logger.ErrorException(message, error ?? new Exception(message)))
                    {
                        IgnoredSeries = () => plugin.Configuration.IgnoredTvdbSeriesIds ?? Array.Empty<int>(),
                    };
                    _sessions.PlaybackStopped += OnPlaybackStopped;
                    _userData.UserDataSaved += OnUserDataSaved;
                    _library.ItemAdded += OnItemAdded;
                    _library.ItemRemoved += OnItemRemoved;
                    _hooked = true;
                    Instance = this;
                    StartupError = null;
                    _logger.Info("Feedrion: started");
                }
                catch (Exception e)
                {
                    StartupError = e.GetType().Name + ": " + e.Message;
                    _logger.ErrorException("Feedrion: could not start", e);
                    _http?.Dispose();
                    return;
                }
            }
            _ = Task.Run(() => LoopAsync(_stop.Token));
        }

        public void Dispose()
        {
            if (_hooked)
            {
                _sessions.PlaybackStopped -= OnPlaybackStopped;
                _userData.UserDataSaved -= OnUserDataSaved;
                _library.ItemAdded -= OnItemAdded;
                _library.ItemRemoved -= OnItemRemoved;
                _hooked = false;
            }
            if (Instance == this) Instance = null;
            _stop.Cancel();
            _http?.Dispose();
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
                if (e.Item is Movie movie)
                {
                    var film = Adapter.MovieReference(movie);
                    if (film == null) return;
                    var played = Scrobble.PlayedPercent(e.PlaybackPositionTicks, movie.RunTimeTicks);
                    foreach (var user in e.Users)
                        Engine.RecordMoviePlayback(EmbyAdapter.UserKey(user), film, played, e.PlayedToCompletion);
                    return;
                }
                if (!(e.Item is Episode episode)) return;
                var reference = Adapter.Reference(episode);
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
            // The reader's own « mark played / unplayed », and Emby's own
            // « played » at the end of a playback — a stop event can carry no
            // position, or one Emby already reset to 0 (owner, 01/11/2026:
            // « j'ai regardé un épisode sur Emby et pourtant ça n'a pas envoyé
            // le vu ; le marquer non vu puis vu l'a fait »).
            var finished = e.SaveReason == UserDataSaveReason.PlaybackFinished;
            if ((e.SaveReason != UserDataSaveReason.TogglePlayed && !finished) || e.User == null) return;
            if (e.Item is Movie movie)
            {
                try
                {
                    var film = Adapter.MovieReference(movie);
                    if (film == null || e.UserData == null) return;
                    var key = EmbyAdapter.UserKey(e.User);
                    if (finished)
                    {
                        if (e.UserData.Played) Engine.RecordMoviePlayedByServer(key, film);
                        return;
                    }
                    Engine.RecordMoviePlayedToggle(key, film, e.UserData.Played);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Feedrion: could not record a played mark", ex);
                }
                return;
            }
            if (!(e.Item is Episode episode)) return;
            try
            {
                var reference = Adapter.Reference(episode);
                if (reference == null || e.UserData == null) return;
                if (finished)
                {
                    if (e.UserData.Played) Engine.RecordPlayedByServer(EmbyAdapter.UserKey(e.User), reference);
                    return;
                }
                Engine.RecordPlayedToggle(EmbyAdapter.UserKey(e.User), reference, e.UserData.Played);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Feedrion: could not record a played mark", ex);
            }
        }

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie)
            {
                try
                {
                    var film = Adapter.MovieItem(e.Item);
                    if (film == null) return;
                    _removedMovies.TryRemove(film.ItemId, out _);
                    _addedMovies[film.ItemId] = film;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Feedrion: could not read an added film", ex);
                }
                return;
            }
            LibraryItem? item;
            try
            {
                item = Adapter.Item(e.Item);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Feedrion: could not read an added episode", ex);
                return;
            }
            if (item == null) return;
            _removed.TryRemove(item.TvdbEpisodeId, out _);
            _added[item.TvdbEpisodeId] = item;
        }

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie)
            {
                var itemId = e.Item.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _addedMovies.TryRemove(itemId, out _);
                _removedMovies[itemId] = 0;
                return;
            }
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
            var addedMovies = new List<MovieLibraryItem>();
            foreach (var key in _addedMovies.Keys)
                if (_addedMovies.TryRemove(key, out var film)) addedMovies.Add(film);
            var removedMovies = new List<string>();
            foreach (var key in _removedMovies.Keys)
                if (_removedMovies.TryRemove(key, out _)) removedMovies.Add(key);
            Engine.RecordMovieLibraryChange(addedMovies, removedMovies);
        }
    }
}
