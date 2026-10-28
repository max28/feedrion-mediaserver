using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feedrion.MediaServer.Core
{
    /// <summary>One server user linked to one Feedrion account.</summary>
    public sealed class LinkState
    {
        public const int MaxPendingEvents = 20000;
        public const int MaxPendingLibrary = 20000;

        [JsonPropertyName("server_user_id")] public string ServerUserId { get; set; } = "";
        [JsonPropertyName("server_user_name")] public string? ServerUserName { get; set; }
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("family_id")] public string? FamilyId { get; set; }
        [JsonPropertyName("linked_at")] public DateTimeOffset? LinkedAt { get; set; }

        // What the last pass learnt.
        [JsonPropertyName("feedrion_name")] public string? FeedrionName { get; set; }
        [JsonPropertyName("premium")] public bool? Premium { get; set; }
        [JsonPropertyName("pricing_url")] public string? PricingUrl { get; set; }
        [JsonPropertyName("last_sync_at")] public DateTimeOffset? LastSyncAt { get; set; }
        [JsonPropertyName("last_error")] public string? LastError { get; set; }

        // The reader's settings, as Feedrion holds them (the app owns them).
        [JsonPropertyName("scrobble_percent")] public int ScrobblePercent { get; set; } = Scrobble.DefaultThresholdPercent;
        [JsonPropertyName("follow_library")] public bool FollowLibrary { get; set; }

        // Set by the server's administrator on the configuration page.
        /// <summary>Whether a watch of a series the Feedrion account does not
        /// follow yet makes it follow it. Off by default: a server user shared
        /// with the family would otherwise fill « Mes séries » with what the
        /// others watch.</summary>
        [JsonPropertyName("add_new_shows")] public bool AddNewShows { get; set; }

        /// <summary>The libraries this link covers; null for all of them.</summary>
        [JsonPropertyName("library_ids")] public List<string>? LibraryIds { get; set; }

        /// <summary>An episode marked unplayed here is never reported as
        /// unwatched to Feedrion. On by default: a « vu » given in Feedrion is
        /// not undone by a server whose profile someone else uses.</summary>
        [JsonPropertyName("never_unwatch")] public bool NeverUnwatch { get; set; } = true;

        // Progress.
        [JsonPropertyName("watch_cursor")] public string? WatchCursor { get; set; }
        [JsonPropertyName("import_done")] public bool ImportDone { get; set; }
        [JsonPropertyName("server_info")] public string? ServerInfoSent { get; set; }
        [JsonPropertyName("library_full_at")] public DateTimeOffset? LibraryFullAt { get; set; }

        // What is still to send — kept across restarts, and across a lapse in
        // the account's Premium: nothing a 402 answers is dropped.
        [JsonPropertyName("pending_events")] public List<WatchEvent> PendingEvents { get; set; } = new List<WatchEvent>();
        [JsonPropertyName("pending_library_added")] public List<LibraryItem> PendingLibraryAdded { get; set; } = new List<LibraryItem>();
        [JsonPropertyName("pending_library_removed")] public List<int> PendingLibraryRemoved { get; set; } = new List<int>();

        [JsonIgnore] public bool IsLinked => !string.IsNullOrEmpty(RefreshToken);

        /// <summary>Whether an episode in [libraries] is this link's business.
        /// With a choice made, an episode whose library is unknown is not.</summary>
        public bool Covers(IReadOnlyList<string>? libraries)
        {
            if (LibraryIds == null) return true;
            if (libraries == null) return false;
            foreach (var id in libraries)
                if (LibraryIds.Contains(id, StringComparer.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Queue an event; a newer event about the same episode
        /// replaces the older one (the last word decides).</summary>
        public void Enqueue(WatchEvent e)
        {
            var key = e.EpisodeKey;
            PendingEvents.RemoveAll(p => p.EpisodeKey == key);
            PendingEvents.Add(e);
            if (PendingEvents.Count > MaxPendingEvents)
                PendingEvents.RemoveRange(0, PendingEvents.Count - MaxPendingEvents);
        }

        public void EnqueueLibrary(IEnumerable<LibraryItem> added, IEnumerable<int> removed)
        {
            foreach (var item in added)
            {
                PendingLibraryRemoved.Remove(item.TvdbEpisodeId);
                PendingLibraryAdded.RemoveAll(a => a.TvdbEpisodeId == item.TvdbEpisodeId);
                PendingLibraryAdded.Add(item);
            }
            foreach (var id in removed)
            {
                PendingLibraryAdded.RemoveAll(a => a.TvdbEpisodeId == id);
                if (!PendingLibraryRemoved.Contains(id)) PendingLibraryRemoved.Add(id);
            }
            if (PendingLibraryAdded.Count > MaxPendingLibrary)
            {
                // Too much to send piecemeal: the next full re-send covers it.
                PendingLibraryAdded.Clear();
                PendingLibraryRemoved.Clear();
                LibraryFullAt = null;
            }
        }
    }

    /// <summary>The links, one JSON file each in the plugin's data folder,
    /// written atomically (temp file, then move).</summary>
    public sealed class LinkStore
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _dir;
        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkState> _links;

        public string DirectoryPath => _dir;

        public LinkStore(string directory)
        {
            _dir = directory;
            Directory.CreateDirectory(_dir);
            _links = new Dictionary<string, LinkState>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(_dir, "link-*.json"))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<LinkState>(File.ReadAllText(path, Encoding.UTF8), Json);
                    if (state != null && state.ServerUserId.Length > 0) _links[state.ServerUserId] = state;
                }
                catch (Exception e) when (e is IOException || e is JsonException)
                {
                    // A damaged file is skipped, never fatal: that user re-links.
                }
            }
        }

        public IReadOnlyList<LinkState> All()
        {
            lock (_gate) return _links.Values.ToList();
        }

        public LinkState? Get(string serverUserId)
        {
            lock (_gate) return _links.TryGetValue(serverUserId, out var s) ? s : null;
        }

        public void Save(LinkState state)
        {
            lock (_gate)
            {
                _links[state.ServerUserId] = state;
                var path = PathFor(state.ServerUserId);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Json), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
        }

        public void Remove(string serverUserId)
        {
            lock (_gate)
            {
                _links.Remove(serverUserId);
                var path = PathFor(serverUserId);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private string PathFor(string serverUserId)
        {
            var safe = new string(serverUserId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            if (safe.Length == 0) throw new ArgumentException("Invalid server user id.", nameof(serverUserId));
            return Path.Combine(_dir, "link-" + safe + ".json");
        }
    }

    /// <summary>A link's tokens, as the API client sees them.</summary>
    internal sealed class LinkSession : ISessionStore
    {
        private readonly LinkState _state;
        private readonly LinkStore _store;

        public LinkSession(LinkState state, LinkStore store)
        {
            _state = state;
            _store = store;
        }

        public string? AccessToken => _state.AccessToken;
        public string? RefreshToken => _state.RefreshToken;

        public void SaveTokens(string accessToken, string refreshToken)
        {
            _state.AccessToken = accessToken;
            if (refreshToken.Length > 0) _state.RefreshToken = refreshToken;
            _store.Save(_state);
        }

        public void Forget()
        {
            _state.AccessToken = null;
            _state.RefreshToken = null;
            _state.LastError = "revoked";
            _store.Save(_state);
        }
    }
}
