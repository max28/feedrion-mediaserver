using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Feedrion.MediaServer.Core
{
    /// <summary>A film as the server knows it, reduced to its ids.</summary>
    public sealed class MovieRef
    {
        public int? TvdbId { get; set; }
        public string? ImdbId { get; set; }
        public string? TmdbId { get; set; }

        /// <summary>The libraries the film sits in; null when unknown.</summary>
        public IReadOnlyList<string>? LibraryIds { get; set; }

        /// <summary>The server's own title, for the local activity log only.</summary>
        public string? Name { get; set; }

        public string? Key => MovieIds.Key(TvdbId, ImdbId, TmdbId);
    }

    /// <summary>What a server offers for films. Separate from
    /// <see cref="IMediaServerAdapter"/> so an adapter written before films
    /// keeps compiling: the engine simply leaves films out for it.</summary>
    public interface IMovieAdapter
    {
        /// <summary>Every film the server holds that carries at least one id.</summary>
        IReadOnlyList<MovieLibraryItem> ListLibraryMovies();

        /// <summary>Every film [serverUserId] has played — the initial import.</summary>
        IReadOnlyList<MovieWatchEvent> ListPlayedMovies(string serverUserId);

        /// <summary>Mark a film played for [serverUserId], and return it; null
        /// when the server does not hold it in a library [covers] accepts, or
        /// it is already played.</summary>
        MovieLibraryItem? MarkMoviePlayed(string serverUserId, RemoteMovieWatch watch, Func<IReadOnlyList<string>?, bool> covers);
    }

    // Films (Feedrion's « Films » extension, 09/11/2026). The same rules as the
    // episodes — threshold, echo suppression, the last word wins, Premium as a
    // whole, libraries chosen per link — with their own queue and progress,
    // so an API from before films (404) costs the films and nothing else.
    public sealed partial class SyncEngine
    {
        public static readonly TimeSpan MoviesRetryAfter = TimeSpan.FromHours(24);

        private IMovieAdapter? Movies => _adapter as IMovieAdapter;

        private static string MovieEchoKey(string key) => "m|" + key;

        private bool IsMovieEcho(string serverUserId, MovieRef movie) =>
            MovieIds.AllKeys(movie.TvdbId, movie.ImdbId, movie.TmdbId)
                .Any(k => IsEcho(serverUserId, MovieEchoKey(k)));

        /// <summary>A film's playback stopped. Counted at the reader's
        /// threshold, like an episode.</summary>
        public void RecordMoviePlayback(string serverUserId, MovieRef movie, double? percent, bool playedToCompletion)
        {
            var state = _store.Get(serverUserId);
            if (state == null || !state.IsLinked || movie.Key == null || IsMovieEcho(serverUserId, movie)) return;
            if (!state.Covers(movie.LibraryIds)) return;
            if (!Scrobble.CountsAsWatched(percent, playedToCompletion, state.ScrobblePercent)) return;
            EnqueueMovie(state, MovieEvent(WatchEvent.Watched, movie, playedToCompletion ? (double?)null : percent));
        }

        /// <summary>The server marked a film played at the end of a playback.</summary>
        public void RecordMoviePlayedByServer(string serverUserId, MovieRef movie) =>
            RecordMoviePlayback(serverUserId, movie, null, playedToCompletion: true);

        /// <summary>The reader marked a film played / unplayed on the server.</summary>
        public void RecordMoviePlayedToggle(string serverUserId, MovieRef movie, bool played)
        {
            var state = _store.Get(serverUserId);
            if (state == null || !state.IsLinked || movie.Key == null || !state.Covers(movie.LibraryIds)) return;
            if (!played && state.NeverUnwatch) return;
            if (played && IsMovieEcho(serverUserId, movie)) return;
            EnqueueMovie(state, MovieEvent(played ? WatchEvent.Watched : WatchEvent.Unwatched, movie, null));
        }

        /// <summary>Films arrived on, or left, the server (by the server's item id).</summary>
        public void RecordMovieLibraryChange(IReadOnlyCollection<MovieLibraryItem> added, IReadOnlyCollection<string> removed)
        {
            if (added.Count == 0 && removed.Count == 0) return;
            lock (_gate)
            {
                foreach (var state in _store.All().Where(s => s.IsLinked))
                {
                    var mine = added.Where(a => state.Covers(a.LibraryIds)).ToList();
                    if (mine.Count == 0 && removed.Count == 0) continue;
                    state.EnqueueMovieLibrary(mine, removed);
                    _store.Save(state);
                }
            }
            Wake();
        }

        private MovieWatchEvent MovieEvent(string type, MovieRef movie, double? percent) => new MovieWatchEvent
        {
            Type = type,
            TvdbId = movie.TvdbId,
            ImdbId = movie.ImdbId,
            TmdbId = movie.TmdbId,
            Name = movie.Name,
            At = Clock(),
            Percent = percent.HasValue ? Math.Round(percent.Value, 1) : (double?)null,
        };

        private readonly Dictionary<string, string> _movieLabels = new Dictionary<string, string>();

        private void RememberMovie(LinkState state, MovieWatchEvent e)
        {
            if (e.Name == null) return;
            lock (_movieLabels) _movieLabels[state.ServerUserId + "|" + e.MovieKey] = e.Name;
        }

        private void EnqueueMovie(LinkState state, MovieWatchEvent e)
        {
            RememberMovie(state, e);
            lock (_gate)
            {
                state.EnqueueMovie(e);
                _store.Save(state);
            }
            Wake();
        }

        private ActivityEntry SentMovie(LinkState state, MovieWatchEvent e)
        {
            string? name;
            lock (_movieLabels)
            {
                var key = state.ServerUserId + "|" + e.MovieKey;
                _movieLabels.TryGetValue(key, out name);
                _movieLabels.Remove(key);
            }
            return new ActivityEntry
            {
                At = Clock(),
                ToFeedrion = true,
                Movie = true,
                Action = e.Type == WatchEvent.Unwatched ? ActivityEntry.Unwatched : ActivityEntry.Watched,
                UserName = state.ServerUserName,
                Series = name ?? e.Name,
            };
        }

        // ── The pass ──────────────────────────────────────────────────────────

        private async Task SyncMoviesAsync(FeedrionClient client, LinkState state, MediaServerConfig config, CancellationToken ct)
        {
            var movies = Movies;
            if (movies == null) return;
            if (state.MoviesUnsupportedAt.HasValue && Clock() - state.MoviesUnsupportedAt.Value < MoviesRetryAfter) return;
            if (string.IsNullOrEmpty(state.MovieWatchCursor)) state.MovieWatchCursor = config.WatchCursor;
            try
            {
                if (!state.MovieImportDone)
                {
                    var history = movies.ListPlayedMovies(state.ServerUserId)
                        .Where(e => state.Covers(e.LibraryIds) && e.MovieKey.Length > 0).ToList();
                    lock (_gate)
                    {
                        var later = state.PendingMovieEvents.ToList();
                        state.PendingMovieEvents.Clear();
                        foreach (var e in history)
                        {
                            RememberMovie(state, e);
                            state.EnqueueMovie(e);
                        }
                        foreach (var e in later) state.EnqueueMovie(e);
                        state.MovieImportDone = true;
                        _store.Save(state);
                    }
                }
                await FlushMovieEventsAsync(client, state, ct).ConfigureAwait(false);
                await SyncMovieLibraryAsync(client, movies, state, ct).ConfigureAwait(false);
                await PullMovieWatchesAsync(client, movies, state, ct).ConfigureAwait(false);
                state.MoviesUnsupportedAt = null;
            }
            catch (FeedrionApiException e) when (e.Status == 404)
            {
                // A Feedrion API from before films: they wait, nothing is lost.
                state.MoviesUnsupportedAt = Clock();
                lock (_gate) _store.Save(state);
            }
        }

        private async Task FlushMovieEventsAsync(FeedrionClient client, LinkState state, CancellationToken ct)
        {
            while (true)
            {
                List<MovieWatchEvent> batch;
                lock (_gate)
                {
                    var dropped = state.PendingMovieEvents.RemoveAll(e =>
                        e.MovieKey.Length == 0 || (state.NeverUnwatch && e.Type == WatchEvent.Unwatched));
                    if (dropped > 0) _store.Save(state);
                    batch = state.PendingMovieEvents.Take(EventsPerCall).ToList();
                }
                if (batch.Count == 0) return;
                await client.PostMovieEventsAsync(batch, state.AddNewMovies, ct).ConfigureAwait(false);
                var logged = batch.Select(e => SentMovie(state, e)).ToList();
                _activity.Add(logged.Skip(Math.Max(0, logged.Count - ActivityLog.Keep)));
                lock (_gate)
                {
                    foreach (var sent in batch) state.PendingMovieEvents.Remove(sent);
                    _store.Save(state);
                }
            }
        }

        private async Task SyncMovieLibraryAsync(FeedrionClient client, IMovieAdapter movies, LinkState state, CancellationToken ct)
        {
            var due = state.MovieLibraryFullAt == null || Clock() - state.MovieLibraryFullAt.Value > LibraryResendEvery;
            if (due)
            {
                var items = movies.ListLibraryMovies().Where(i => state.Covers(i.LibraryIds)).ToList();
                var generation = Clock().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var pages = items.Count == 0 ? 1 : (items.Count + LibraryPerCall - 1) / LibraryPerCall;
                for (var page = 0; page < pages; page++)
                {
                    await client.PostMovieLibraryAsync(new MovieLibraryBatch
                    {
                        Items = items.Skip(page * LibraryPerCall).Take(LibraryPerCall).ToList(),
                        Generation = generation,
                        Final = page == pages - 1,
                    }, ct).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    state.MovieLibraryFullAt = Clock();
                    state.PendingMovieLibraryAdded.Clear();
                    state.PendingMovieLibraryRemoved.Clear();
                    _store.Save(state);
                }
                return;
            }
            while (true)
            {
                List<MovieLibraryItem> added;
                List<string> removed;
                lock (_gate)
                {
                    added = state.PendingMovieLibraryAdded.Take(LibraryPerCall).ToList();
                    removed = state.PendingMovieLibraryRemoved.Take(LibraryPerCall).ToList();
                }
                if (added.Count == 0 && removed.Count == 0) return;
                await client.PostMovieLibraryAsync(new MovieLibraryBatch { Items = added, Removed = removed }, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var a in added) state.PendingMovieLibraryAdded.Remove(a);
                    foreach (var r in removed) state.PendingMovieLibraryRemoved.Remove(r);
                    _store.Save(state);
                }
            }
        }

        private async Task PullMovieWatchesAsync(FeedrionClient client, IMovieAdapter movies, LinkState state, CancellationToken ct)
        {
            for (var page = 0; page < WatchPagesPerPass; page++)
            {
                var watches = await client.GetMovieWatchedAsync(state.MovieWatchCursor, WatchesPerCall, ct).ConfigureAwait(false);
                var applied = new List<ActivityEntry>();
                foreach (var watch in watches.Watches)
                {
                    // Before the server is touched: its own event comes back
                    // from inside MarkMoviePlayed on some servers.
                    foreach (var key in MovieIds.AllKeys(watch.TvdbId, watch.ImdbId, watch.TmdbId))
                        _echoes[state.ServerUserId + "|" + MovieEchoKey(key)] = Clock();
                    try
                    {
                        var marked = movies.MarkMoviePlayed(state.ServerUserId, watch, state.Covers);
                        if (marked != null) applied.Add(new ActivityEntry
                        {
                            At = Clock(),
                            ToFeedrion = false,
                            Movie = true,
                            Action = ActivityEntry.Watched,
                            UserName = state.ServerUserName,
                            Series = marked.Name,
                        });
                    }
                    catch (Exception e)
                    {
                        _log("Feedrion: could not mark a film played", e);
                    }
                }
                _activity.Add(applied);
                lock (_gate)
                {
                    state.MovieWatchCursor = watches.Cursor;
                    _store.Save(state);
                }
                if (!watches.More) return;
            }
        }
    }
}
