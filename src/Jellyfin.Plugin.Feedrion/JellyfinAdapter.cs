using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Feedrion.MediaServer.Core;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Feedrion
{
    /// <summary>The engine's view of Jellyfin. Everything that leaves is a TheTVDB
    /// id or a Jellyfin item id — never a title, a file name or a path.</summary>
    public sealed class JellyfinAdapter : IMediaServerAdapter
    {
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IUserDataManager _userData;
        private readonly IServerApplicationHost _host;
        private readonly object _gate = new object();
        private Dictionary<string, Guid>? _index;
        private DateTimeOffset _indexedAt;

        public JellyfinAdapter(
            ILibraryManager library, IUserManager users, IUserDataManager userData, IServerApplicationHost host)
        {
            _library = library;
            _users = users;
            _userData = userData;
            _host = host;
        }

        public string Kind => "jellyfin";

        public static string UserKey(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);

        /// <summary>The episode's TheTVDB identity, or null when its series has none
        /// (nothing to match it on in Feedrion).</summary>
        public static EpisodeRef? ToRef(BaseItem item)
        {
            if (item is not Episode episode) return null;
            var seriesTvdb = ParseId(episode.Series?.GetProviderId(MetadataProvider.Tvdb));
            if (seriesTvdb == null) return null;
            return new EpisodeRef
            {
                TvdbSeriesId = seriesTvdb.Value,
                TvdbEpisodeId = ParseId(episode.GetProviderId(MetadataProvider.Tvdb)),
                Season = episode.ParentIndexNumber,
                Number = episode.IndexNumber,
            };
        }

        public static LibraryItem? ToLibraryItem(BaseItem item)
        {
            var reference = ToRef(item);
            if (reference?.TvdbEpisodeId == null || item is not Episode episode) return null;
            return new LibraryItem
            {
                TvdbSeriesId = reference.TvdbSeriesId,
                TvdbEpisodeId = reference.TvdbEpisodeId.Value,
                ItemId = episode.Id.ToString("N", CultureInfo.InvariantCulture),
                SeriesItemId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId.ToString("N", CultureInfo.InvariantCulture),
                Season = reference.Season,
                Number = reference.Number,
            };
        }

        private static int? ParseId(string? raw) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

        public ServerInfo DescribeServer() => new ServerInfo
        {
            ServerId = _host.SystemId,
            ServerName = _host.FriendlyName,
            ServerUrl = string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.PublicServerUrl)
                ? null
                : Plugin.Instance!.Configuration.PublicServerUrl.Trim(),
            ClientVersion = "jellyfin-" + typeof(JellyfinAdapter).Assembly.GetName().Version?.ToString(3),
        };

        private IReadOnlyList<BaseItem> Episodes(User? user, bool? played) => _library.GetItemList(
            new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true,
                IsPlayed = played,
            });

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
                    At = data?.LastPlayedDate is DateTime at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
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
            data.LastPlayedDate = watch.WatchedAt.UtcDateTime;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
            return true;
        }

        private Guid? Lookup(RemoteWatch watch)
        {
            Dictionary<string, Guid>? index;
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

        private static Dictionary<string, Guid> BuildIndex(IEnumerable<LibraryItem> items)
        {
            var index = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!Guid.TryParse(item.ItemId, out var id)) continue;
                index["e" + item.TvdbEpisodeId] = id;
                if (item.Season.HasValue && item.Number.HasValue)
                    index["s" + item.TvdbSeriesId + ":" + item.Season + ":" + item.Number] = id;
            }
            return index;
        }

        private User? FindUser(string serverUserId) =>
            Guid.TryParse(serverUserId, out var id) ? _users.GetUserById(id) : null;
    }
}
