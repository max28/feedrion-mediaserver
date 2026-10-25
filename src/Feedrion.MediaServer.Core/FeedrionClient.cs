using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Feedrion.MediaServer.Core
{
    /// <summary>Where a linked user's session lives. The plugin persists it in its
    /// own data folder; the client only reads and rotates it.</summary>
    public interface ISessionStore
    {
        string? AccessToken { get; }
        string? RefreshToken { get; }
        void SaveTokens(string accessToken, string refreshToken);
        /// <summary>Feedrion says this session no longer exists (revoked from the app).</summary>
        void Forget();
    }

    /// <summary>A Feedrion answer that is not a success.</summary>
    public class FeedrionApiException : Exception
    {
        public FeedrionApiException(int status, string? detail, TimeSpan? retryAfter = null)
            : base("Feedrion answered " + status + (string.IsNullOrEmpty(detail) ? "" : ": " + detail))
        {
            Status = status;
            Detail = detail;
            RetryAfter = retryAfter;
        }

        public int Status { get; }
        public string? Detail { get; }
        public TimeSpan? RetryAfter { get; }

        /// <summary>The account is not Premium (any more): keep the events, send nothing.</summary>
        public bool IsPremiumRequired => Status == 402;

        /// <summary>The session is gone: revoked from the app, or never valid.</summary>
        public bool IsUnauthorized => Status == 401;

        /// <summary>Worth retrying later, unchanged.</summary>
        public bool IsTransient => Status == 0 || Status == 429 || Status >= 500;
    }

    /// <summary>The Feedrion integration API, for ONE linked user.</summary>
    public sealed class FeedrionClient
    {
        public const string DefaultBaseUrl = "https://api.feedrion.com/api";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly HttpClient _http;
        private readonly string _base;
        private readonly ISessionStore? _session;
        private readonly SemaphoreSlim _refreshing = new SemaphoreSlim(1, 1);

        public FeedrionClient(HttpClient http, string? baseUrl, ISessionStore? session)
        {
            _http = http;
            _base = NormalizeBaseUrl(baseUrl);
            _session = session;
        }

        /// <summary>An https API root, without its trailing slash; the default
        /// otherwise. Plain http is refused: a token would travel in clear.</summary>
        public static string NormalizeBaseUrl(string? baseUrl)
        {
            var value = (baseUrl ?? "").Trim().TrimEnd('/');
            if (value.Length == 0) return DefaultBaseUrl;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("The Feedrion API address must be an https URL.", nameof(baseUrl));
            return value;
        }

        // ── Pairing (no session yet) ──────────────────────────────────────────

        public Task<PairingStart> StartPairingAsync(string kind, string? label, CancellationToken ct) =>
            SendAsync<PairingStart>(HttpMethod.Post, "/integrations/v1/pair/start",
                new { kind, label = Trim(label, 60) }, authenticated: false, ct);

        public async Task<PairingPoll> PollPairingAsync(string deviceCode, CancellationToken ct)
        {
            try
            {
                return await SendAsync<PairingPoll>(HttpMethod.Post, "/integrations/v1/pair/poll",
                    new { device_code = deviceCode }, authenticated: false, ct).ConfigureAwait(false);
            }
            catch (FeedrionApiException e) when (e.Status == 410)
            {
                return new PairingPoll { Status = "expired" };
            }
        }

        // ── The linked session ────────────────────────────────────────────────

        public Task<IntegrationMe> GetMeAsync(CancellationToken ct) =>
            SendAsync<IntegrationMe>(HttpMethod.Get, "/integrations/v1/me", null, true, ct);

        public Task<MediaServerConfig> GetConfigAsync(CancellationToken ct) =>
            SendAsync<MediaServerConfig>(HttpMethod.Get, "/integrations/v1/mediaserver/config", null, true, ct);

        public Task<ServerInfo> PutServerAsync(ServerInfo info, CancellationToken ct) =>
            SendAsync<ServerInfo>(HttpMethod.Put, "/integrations/v1/mediaserver/server", info, true, ct);

        public Task<EventsResult> PostEventsAsync(System.Collections.Generic.IList<WatchEvent> events, CancellationToken ct) =>
            SendAsync<EventsResult>(HttpMethod.Post, "/integrations/v1/mediaserver/events", new { events }, true, ct);

        public Task<LibraryResult> PostLibraryAsync(LibraryBatch batch, CancellationToken ct) =>
            SendAsync<LibraryResult>(HttpMethod.Post, "/integrations/v1/mediaserver/library", batch, true, ct);

        public Task<WatchesPage> GetWatchedAsync(string? cursor, int limit, CancellationToken ct) =>
            SendAsync<WatchesPage>(HttpMethod.Get,
                "/integrations/v1/mediaserver/watched?limit=" + limit
                + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor)),
                null, true, ct);

        public async Task DisconnectAsync(CancellationToken ct)
        {
            await SendAsync<object>(HttpMethod.Post, "/integrations/v1/disconnect", null, true, ct).ConfigureAwait(false);
        }

        // ── Transport ─────────────────────────────────────────────────────────

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
        {
            var response = await SendOnceAsync(method, path, body, authenticated, ct).ConfigureAwait(false);
            if (authenticated && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                // An expired access token, most of the time: rotate once and retry.
                if (await RefreshAsync(ct).ConfigureAwait(false))
                    response = await SendOnceAsync(method, path, body, true, ct).ConfigureAwait(false);
                else
                    throw new FeedrionApiException(401, "session_expired");
            }
            using (response)
            {
                var text = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status >= 200 && status < 300)
                {
                    if (string.IsNullOrWhiteSpace(text) || typeof(T) == typeof(object)) return default!;
                    return JsonSerializer.Deserialize<T>(text, Json)!;
                }
                if (authenticated && status == 401) _session?.Forget();
                throw new FeedrionApiException(status, ReadDetail(text), response.Headers.RetryAfter?.Delta);
            }
        }

        private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(method, _base + path))
            {
                if (authenticated)
                {
                    var token = _session?.AccessToken;
                    if (string.IsNullOrEmpty(token)) throw new FeedrionApiException(401, "not_linked");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                if (body != null)
                    request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
                try
                {
                    return await _http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    throw new FeedrionApiException(0, e.Message);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new FeedrionApiException(0, "timeout");
                }
            }
        }

        /// <summary>Rotate the refresh token. False when Feedrion no longer knows
        /// the session — it was revoked, so it is forgotten here too.</summary>
        private async Task<bool> RefreshAsync(CancellationToken ct)
        {
            if (_session == null || string.IsNullOrEmpty(_session.RefreshToken)) return false;
            await _refreshing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _base + "/auth/refresh"))
                {
                    request.Content = new StringContent(
                        JsonSerializer.Serialize(new { refresh_token = _session.RefreshToken }), Encoding.UTF8, "application/json");
                    HttpResponseMessage response;
                    try
                    {
                        response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    }
                    catch (HttpRequestException e)
                    {
                        throw new FeedrionApiException(0, e.Message);
                    }
                    using (response)
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            _session.Forget();
                            return false;
                        }
                        if (!response.IsSuccessStatusCode)
                            throw new FeedrionApiException((int)response.StatusCode, ReadDetail(text));
                        var pair = JsonSerializer.Deserialize<TokenPair>(text, Json);
                        if (pair == null || pair.AccessToken.Length == 0) return false;
                        _session.SaveTokens(pair.AccessToken, pair.RefreshToken);
                        return true;
                    }
                }
            }
            finally
            {
                _refreshing.Release();
            }
        }

        private static string? ReadDetail(string text)
        {
            try
            {
                var body = JsonSerializer.Deserialize<ErrorBody>(text, Json);
                return body?.Detail is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Trim(string? value, int max)
        {
            if (value == null) return null;
            var single = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return single.Length <= max ? single : single.Substring(0, max);
        }
    }
}
