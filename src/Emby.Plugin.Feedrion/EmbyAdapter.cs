using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Emby.Plugin.Feedrion
{
    /// <summary>The engine's view of Emby. Only TheTVDB ids and Emby item ids leave.</summary>
    public sealed class EmbyAdapter : IMediaServerAdapter
    {
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IUserDataManager _userData;
        private readonly IServerApplicationHost _host;
        private readonly object _gate = new object();
        private Dictionary<string, LibraryItem>? _index;
        private DateTimeOffset _indexedAt;
        // Which libraries a series sits in, for the live events (ten minutes
        // of staleness rather than a tree walk on every playback).
        private readonly Dictionary<long, string[]> _seriesLibraries = new Dictionary<long, string[]>();
        private DateTimeOffset _seriesLibrariesAt;

        public EmbyAdapter(ILibraryManager library, IUserManager users, IUserDataManager userData, IServerApplicationHost host)
        {
            _library = library;
            _users = users;
            _userData = userData;
            _host = host;
        }

        public string Kind => "emby";

        public static string UserKey(User user) => user.Id.ToString("N", CultureInfo.InvariantCulture);

        public static EpisodeRef? ToRef(BaseItem? item)
        {
            if (!(item is Episode episode)) return null;
            var seriesTvdb = ParseId(episode.Series?.GetProviderId(MetadataProviders.Tvdb));
            if (seriesTvdb == null) return null;
            return new EpisodeRef
            {
                TvdbSeriesId = seriesTvdb.Value,
                TvdbEpisodeId = ParseId(episode.GetProviderId(MetadataProviders.Tvdb)),
                Season = episode.ParentIndexNumber,
                Number = episode.IndexNumber,
                SeriesName = episode.SeriesName,
                EpisodeName = episode.Name,
            };
        }

        public static LibraryItem? ToLibraryItem(BaseItem? item)
        {
            var reference = ToRef(item);
            if (reference?.TvdbEpisodeId == null || !(item is Episode episode)) return null;
            return new LibraryItem
            {
                TvdbSeriesId = reference.TvdbSeriesId,
                TvdbEpisodeId = reference.TvdbEpisodeId.Value,
                // Emby's web app addresses items by their internal number.
                ItemId = episode.InternalId.ToString(CultureInfo.InvariantCulture),
                SeriesItemId = episode.Series == null ? null : episode.Series.InternalId.ToString(CultureInfo.InvariantCulture),
                Season = reference.Season,
                Number = reference.Number,
                SeriesName = reference.SeriesName,
                EpisodeName = reference.EpisodeName,
            };
        }

        /// <summary>[ToRef], with the libraries the episode sits in.</summary>
        public EpisodeRef? Reference(BaseItem? item)
        {
            var reference = ToRef(item);
            if (reference != null) reference.LibraryIds = LibrariesOf((Episode)item!, null);
            return reference;
        }

        /// <summary>[ToLibraryItem], with the libraries the episode sits in.</summary>
        public LibraryItem? Item(BaseItem? item)
        {
            var libraryItem = ToLibraryItem(item);
            if (libraryItem != null) libraryItem.LibraryIds = LibrariesOf((Episode)item!, null);
            return libraryItem;
        }

        // Emby's VirtualFolderInfo.Guid is the library folder's Guid, the id
        // GetCollectionFolders answers with.
        public IReadOnlyList<MediaLibrary> ListLibraries() => _library.GetVirtualFolders()
            .Select(f => new MediaLibrary
            {
                Id = Guid.TryParse(f.Guid, out var id) ? LibraryKey(id) : "",
                Name = f.Name ?? "",
                Type = f.CollectionType,
            })
            .Where(l => l.Id.Length > 0)
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        private static string LibraryKey(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

        /// <summary>The libraries [episode] sits in — those of its series.</summary>
        private string[] LibrariesOf(Episode episode, Dictionary<long, string[]>? scan)
        {
            BaseItem anchor = (BaseItem?)episode.Series ?? episode;
            var key = anchor.InternalId;
            if (scan != null)
            {
                if (!scan.TryGetValue(key, out var found)) scan[key] = found = Compute(anchor);
                return found;
            }
            lock (_gate)
            {
                if (DateTimeOffset.UtcNow - _seriesLibrariesAt > TimeSpan.FromMinutes(10))
                {
                    _seriesLibraries.Clear();
                    _seriesLibrariesAt = DateTimeOffset.UtcNow;
                }
                if (_seriesLibraries.TryGetValue(key, out var cached)) return cached;
            }
            var computed = Compute(anchor);
            lock (_gate) _seriesLibraries[key] = computed;
            return computed;
        }

        private string[] Compute(BaseItem item)
        {
            try
            {
                return _library.GetCollectionFolders(item).Select(f => LibraryKey(f.Id)).Distinct().ToArray();
            }
            catch (Exception)
            {
                // Unknown library: a link that chose its libraries leaves it out.
                return Array.Empty<string>();
            }
        }

        private static int? ParseId(string? raw) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : (int?)null;

        public ServerInfo DescribeServer()
        {
            var url = Plugin.Instance?.Configuration.PublicServerUrl;
            return new ServerInfo
            {
                ServerId = _host.SystemId,
                ServerName = _host.FriendlyName,
                ServerUrl = string.IsNullOrWhiteSpace(url) ? null : url!.Trim(),
                ClientVersion = "emby-" + typeof(EmbyAdapter).Assembly.GetName().Version?.ToString(3),
            };
        }

        private BaseItem[] Episodes(User? user, bool? played)
        {
            var query = user == null ? new InternalItemsQuery() : new InternalItemsQuery(user);
            query.IncludeItemTypes = new[] { nameof(Episode) };
            query.IsVirtualItem = false;
            query.Recursive = true;
            query.IsPlayed = played;
            return _library.GetItemList(query);
        }

        public IReadOnlyList<MediaSeries> ListSeries() => _library.GetItemList(
                new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Series) }, Recursive = true })
            .Select(i => (Item: i, Id: ParseId(i.GetProviderId(MetadataProviders.Tvdb))))
            .Where(x => x.Id != null)
            .GroupBy(x => x.Id!.Value)
            .Select(g => new MediaSeries { TvdbId = g.Key, Name = g.First().Item.Name ?? "", Year = g.First().Item.ProductionYear })
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        public IReadOnlyList<LibraryItem> ListLibraryEpisodes()
        {
            var scan = new Dictionary<long, string[]>();
            var items = new List<LibraryItem>();
            foreach (var episode in Episodes(null, null).OfType<Episode>())
            {
                var item = ToLibraryItem(episode);
                if (item == null) continue;
                item.LibraryIds = LibrariesOf(episode, scan);
                items.Add(item);
            }
            lock (_gate)
            {
                _index = BuildIndex(items);
                _indexedAt = DateTimeOffset.UtcNow;
            }
            return items;
        }

        public IReadOnlyList<WatchEvent> ListPlayedEpisodes(string serverUserId)
        {
            var user = FindUser(serverUserId);
            if (user == null) return Array.Empty<WatchEvent>();
            var events = new List<WatchEvent>();
            var scan = new Dictionary<long, string[]>();
            foreach (var item in Episodes(user, true))
            {
                var reference = ToRef(item);
                if (reference == null) continue;
                var data = _userData.GetUserData(user, item);
                events.Add(new WatchEvent
                {
                    Type = WatchEvent.Watched,
                    TvdbSeriesId = reference.TvdbSeriesId,
                    TvdbEpisodeId = reference.TvdbEpisodeId,
                    Season = reference.TvdbEpisodeId.HasValue ? null : reference.Season,
                    Episode = reference.TvdbEpisodeId.HasValue ? null : reference.Number,
                    SeriesName = reference.SeriesName,
                    EpisodeName = reference.EpisodeName,
                    LocalSeason = reference.Season,
                    LocalNumber = reference.Number,
                    At = data?.LastPlayedDate,
                    LibraryIds = LibrariesOf((Episode)item, scan),
                });
            }
            return events.OrderBy(e => e.At ?? DateTimeOffset.MinValue).ToList();
        }

        public LibraryItem? MarkPlayed(string serverUserId, RemoteWatch watch, Func<IReadOnlyList<string>?, bool> covers)
        {
            var user = FindUser(serverUserId);
            if (user == null) return null;
            var held = Lookup(watch);
            if (held == null || !covers(held.LibraryIds)
                || !long.TryParse(held.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)) return null;
            var item = _library.GetItemById(itemId);
            if (item == null) return null;
            var data = _userData.GetUserData(user, item);
            if (data == null || data.Played) return null;
            data.Played = true;
            data.PlayCount = Math.Max(1, data.PlayCount);
            data.LastPlayedDate = watch.WatchedAt;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
            return held;
        }

        public LibraryItem? Find(int tvdbSeriesId, int? tvdbEpisodeId, int? season, int? number)
        {
            Dictionary<string, LibraryItem>? index;
            lock (_gate) index = _index;
            if (index == null) return null;
            if (tvdbEpisodeId.HasValue && index.TryGetValue("e" + tvdbEpisodeId.Value, out var byId)) return byId;
            if (season.HasValue && number.HasValue
                && index.TryGetValue("s" + tvdbSeriesId + ":" + season + ":" + number, out var byNumber)) return byNumber;
            return null;
        }

        private LibraryItem? Lookup(RemoteWatch watch)
        {
            Dictionary<string, LibraryItem>? index;
            lock (_gate)
            {
                index = _index;
                if (index == null || DateTimeOffset.UtcNow - _indexedAt > TimeSpan.FromMinutes(10)) index = null;
            }
            if (index == null)
            {
                ListLibraryEpisodes();
                lock (_gate) index = _index;
            }
            if (index == null) return null;
            if (index.TryGetValue("e" + watch.TvdbEpisodeId, out var byId)) return byId;
            return index.TryGetValue("s" + watch.TvdbSeriesId + ":" + watch.Season + ":" + watch.Episode, out var byNumber)
                ? byNumber : null;
        }

        private static Dictionary<string, LibraryItem> BuildIndex(IEnumerable<LibraryItem> items)
        {
            var index = new Dictionary<string, LibraryItem>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                index["e" + item.TvdbEpisodeId] = item;
                if (item.Season.HasValue && item.Number.HasValue)
                    index["s" + item.TvdbSeriesId + ":" + item.Season + ":" + item.Number] = item;
            }
            return index;
        }

        public User? FindUser(string serverUserId) =>
            Guid.TryParse(serverUserId, out var id) ? _users.GetUserById(id) : null;
    }
}
