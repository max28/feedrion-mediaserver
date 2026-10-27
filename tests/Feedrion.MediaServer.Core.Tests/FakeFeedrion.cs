using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Feedrion.MediaServer.Core.Tests
{
    /// <summary>A stand-in for the Feedrion API that answers the way the real
    /// routes do, and records what it was sent.</summary>
    public sealed class FakeFeedrion : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Body, string? Bearer)> Calls =
            new List<(string, string, string?, string?)>();

        public bool Premium { get; set; } = true;
        public string ValidAccess { get; set; } = "access-1";
        public string ValidRefresh { get; set; } = "refresh-1";
        public bool RefreshRevoked { get; set; }
        public int ScrobblePercent { get; set; } = 90;
        public List<RemoteWatch> Watches { get; } = new List<RemoteWatch>();
        public List<List<WatchEvent>> EventBatches { get; } = new List<List<WatchEvent>>();
        public List<bool?> EventBatchAddNewShows { get; } = new List<bool?>();
        public List<LibraryBatch> LibraryBatches { get; } = new List<LibraryBatch>();
        public string PairStatus { get; set; } = "pending";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.PathAndQuery;
            var bearer = request.Headers.Authorization?.Parameter;
            Calls.Add((request.Method.Method, path, body, bearer));
            if (path.StartsWith("/api/integrations/v1/pair/start"))
                return Json(new { device_code = "dev-code-1234567890", user_code = "ABCD-EFGH",
                    verification_url = "https://feedrion.example/settings/integrations/connect?code=ABCD-EFGH",
                    expires_in = 600, interval = 2 });
            if (path.StartsWith("/api/integrations/v1/pair/poll"))
                return PairStatus == "approved"
                    ? Json(new { status = "approved", kind = "jellyfin", access_token = ValidAccess,
                        refresh_token = ValidRefresh, family_id = "fam" })
                    : Json(new { status = "pending" });
            if (path.StartsWith("/api/auth/refresh"))
            {
                if (RefreshRevoked) return Status(401, "Invalid refresh token");
                ValidAccess = "access-2";
                ValidRefresh = "refresh-2";
                return Json(new { access_token = ValidAccess, refresh_token = ValidRefresh, token_type = "bearer" });
            }
            if (bearer != ValidAccess) return Status(401, "Invalid credentials");
            if (path == "/api/integrations/v1/me")
                return Json(new { kind = "jellyfin", display_name = "Max", language = "fr", premium = Premium,
                    pricing_url = "https://feedrion.example/pricing" });
            if (!Premium) return Status(402, "premium_required");
            if (path.StartsWith("/api/integrations/v1/mediaserver/config"))
                return Json(new { scrobble_percent = ScrobblePercent, follow_library = false,
                    tvdb_available = true, watch_cursor = "start|0" });
            if (path.StartsWith("/api/integrations/v1/mediaserver/server")) return Json(new { });
            if (path.StartsWith("/api/integrations/v1/mediaserver/events"))
            {
                var doc = JsonDocument.Parse(body!);
                EventBatches.Add(JsonSerializer.Deserialize<List<WatchEvent>>(doc.RootElement.GetProperty("events").GetRawText())!);
                EventBatchAddNewShows.Add(doc.RootElement.TryGetProperty("add_new_shows", out var add) ? add.GetBoolean() : (bool?)null);
                return Json(new { accepted = 1, ignored = 0 });
            }
            if (path.StartsWith("/api/integrations/v1/mediaserver/library"))
            {
                LibraryBatches.Add(JsonSerializer.Deserialize<LibraryBatch>(body!)!);
                return Json(new { stored = 1, removed = 0, followed = 0 });
            }
            if (path.StartsWith("/api/integrations/v1/mediaserver/watched"))
            {
                var page = Watches.ToList();
                Watches.Clear();
                return Json(new { watches = page, cursor = "next|" + page.Count, more = false });
            }
            if (path.StartsWith("/api/integrations/v1/disconnect")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Status(404, "not_found");
        }

        private static HttpResponseMessage Json(object value) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Status(int status, string detail) => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { detail }), Encoding.UTF8, "application/json"),
        };
    }

    public sealed class FakeAdapter : IMediaServerAdapter
    {
        public string Kind => "jellyfin";
        public List<LibraryItem> Library { get; } = new List<LibraryItem>();
        public List<WatchEvent> History { get; } = new List<WatchEvent>();
        public List<RemoteWatch> Marked { get; } = new List<RemoteWatch>();
        public List<MediaLibrary> Libraries { get; } = new List<MediaLibrary>
        {
            new MediaLibrary { Id = "lib-shows", Name = "Shows", Type = "tvshows" },
            new MediaLibrary { Id = "lib-kids", Name = "Kids", Type = "tvshows" },
        };

        public ServerInfo DescribeServer() => new ServerInfo { ServerId = "srv", ServerName = "Salon", ClientVersion = "test" };
        public IReadOnlyList<MediaLibrary> ListLibraries() => Libraries;
        public IReadOnlyList<MediaSeries> ListSeries() => new[] { new MediaSeries { TvdbId = 500, Name = "Northwind" } };
        public IReadOnlyList<LibraryItem> ListLibraryEpisodes() => Library;
        public IReadOnlyList<WatchEvent> ListPlayedEpisodes(string serverUserId) => History;

        /// <summary>The library each series sits in, for MarkPlayed.</summary>
        public Dictionary<int, string> SeriesLibrary { get; } = new Dictionary<int, string>();

        public bool MarkPlayed(string serverUserId, RemoteWatch watch, Func<IReadOnlyList<string>?, bool> covers)
        {
            var library = SeriesLibrary.TryGetValue(watch.TvdbSeriesId, out var l) ? new[] { l } : null;
            if (!covers(library)) return false;
            Marked.Add(watch);
            return true;
        }
    }
}
