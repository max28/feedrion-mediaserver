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
        private Dictionary<string, long>? _index;
        private DateTimeOffset _indexedAt;

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
            };
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

        public IReadOnlyList<LibraryItem> ListLibraryEpisodes()
        {
            var items = Episodes(null, null).Select(ToLibraryItem).Where(i => i != null).Select(i => i!).ToList();
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
                    At = data?.LastPlayedDate,
                });
            }
            return events.OrderBy(e => e.At ?? DateTimeOffset.MinValue).ToList();
        }

        public bool MarkPlayed(string serverUserId, RemoteWatch watch)
        {
            var user = FindUser(serverUserId);
            if (user == null) return false;
            var itemId = Lookup(watch);
            if (itemId == null) return false;
            var item = _library.GetItemById(itemId.Value);
            if (item == null) return false;
            var data = _userData.GetUserData(user, item);
            if (data == null || data.Played) return false;
            data.Played = true;
            data.PlayCount = Math.Max(1, data.PlayCount);
            data.LastPlayedDate = watch.WatchedAt;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
            return true;
        }

        private long? Lookup(RemoteWatch watch)
        {
            Dictionary<string, long>? index;
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
                ? byNumber : (long?)null;
        }

        private static Dictionary<string, long> BuildIndex(IEnumerable<LibraryItem> items)
        {
            var index = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!long.TryParse(item.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                index["e" + item.TvdbEpisodeId] = id;
                if (item.Season.HasValue && item.Number.HasValue)
                    index["s" + item.TvdbSeriesId + ":" + item.Season + ":" + item.Number] = id;
            }
            return index;
        }

        public User? FindUser(string serverUserId) =>
            Guid.TryParse(serverUserId, out var id) ? _users.GetUserById(id) : null;
    }
}
