using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Feedrion.MediaServer.Core
{
    // The shapes of Feedrion's public integration contract (/integrations/v1).
    // Additive on the server side; every field read here tolerates absence.

    public sealed class PairingStart
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
        [JsonPropertyName("verification_url")] public string VerificationUrl { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; } = 3;
    }

    public sealed class PairingPoll
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "pending";
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("family_id")] public string? FamilyId { get; set; }

        [JsonIgnore] public bool IsApproved => Status == "approved" && AccessToken != null && RefreshToken != null;
    }

    public sealed class IntegrationMe
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        [JsonPropertyName("language")] public string Language { get; set; } = "en";
        [JsonPropertyName("premium")] public bool Premium { get; set; }
        [JsonPropertyName("trial_ends_at")] public DateTimeOffset? TrialEndsAt { get; set; }
        [JsonPropertyName("pricing_url")] public string? PricingUrl { get; set; }
    }

    public sealed class MediaServerConfig
    {
        [JsonPropertyName("scrobble_percent")] public int ScrobblePercent { get; set; } = 90;
        [JsonPropertyName("follow_library")] public bool FollowLibrary { get; set; }
        [JsonPropertyName("tvdb_available")] public bool TvdbAvailable { get; set; } = true;
        [JsonPropertyName("watch_cursor")] public string WatchCursor { get; set; } = "";
    }

    public sealed class ServerInfo
    {
        [JsonPropertyName("server_id")] public string? ServerId { get; set; }
        [JsonPropertyName("server_name")] public string? ServerName { get; set; }
        [JsonPropertyName("server_url")] public string? ServerUrl { get; set; }
        [JsonPropertyName("client_version")] public string? ClientVersion { get; set; }
    }

    /// <summary>A played or un-played episode, by TheTVDB ids — never a title or a path.</summary>
    public sealed class WatchEvent
    {
        public const string Watched = "watched";
        public const string Unwatched = "unwatched";

        [JsonPropertyName("type")] public string Type { get; set; } = Watched;
        [JsonPropertyName("tvdb_series_id")] public int TvdbSeriesId { get; set; }
        [JsonPropertyName("tvdb_episode_id")] public int? TvdbEpisodeId { get; set; }
        [JsonPropertyName("season")] public int? Season { get; set; }
        [JsonPropertyName("episode")] public int? Episode { get; set; }
        [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
        [JsonPropertyName("percent")] public double? Percent { get; set; }

        /// <summary>The libraries the episode sits in, to hold the history
        /// import to a link's choice. Never sent, never stored.</summary>
        [JsonIgnore] public IReadOnlyList<string>? LibraryIds { get; set; }

        /// <summary>The same episode, whatever the event says about it.</summary>
        [JsonIgnore]
        public string EpisodeKey => TvdbEpisodeId.HasValue
            ? "e" + TvdbEpisodeId.Value
            : "s" + TvdbSeriesId + ":" + Season + ":" + Episode;
    }

    public sealed class EventsResult
    {
        [JsonPropertyName("accepted")] public int Accepted { get; set; }
        [JsonPropertyName("ignored")] public int Ignored { get; set; }
    }

    /// <summary>One episode the server holds. The server's own ids make the link
    /// « Ouvrir dans Jellyfin » open the right page; nothing else leaves.</summary>
    public sealed class LibraryItem
    {
        [JsonPropertyName("tvdb_series_id")] public int TvdbSeriesId { get; set; }
        [JsonPropertyName("tvdb_episode_id")] public int TvdbEpisodeId { get; set; }
        [JsonPropertyName("item_id")] public string ItemId { get; set; } = "";
        [JsonPropertyName("series_item_id")] public string? SeriesItemId { get; set; }

        // Kept locally to match a reverse-synced watch that has no episode id;
        // never sent (the API matches on ids).
        [JsonIgnore] public int? Season { get; set; }
        [JsonIgnore] public int? Number { get; set; }

        // The libraries the episode sits in: what a link's choice of libraries
        // is checked against. Never sent.
        [JsonIgnore] public IReadOnlyList<string>? LibraryIds { get; set; }
    }

    /// <summary>One library of the server, for the configuration page. Its name
    /// never leaves the server.</summary>
    public sealed class MediaLibrary
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    /// <summary>One series of the server, for the « ignored series » list. Its
    /// name never leaves the server.</summary>
    public sealed class MediaSeries
    {
        [JsonPropertyName("tvdbId")] public int TvdbId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("year")] public int? Year { get; set; }
    }

    /// <summary>What the configuration page sets on one server user's link.</summary>
    public sealed class LinkSettings
    {
        [JsonPropertyName("addNewShows")] public bool AddNewShows { get; set; }

        /// <summary>Null: every library, including the ones added later.</summary>
        [JsonPropertyName("libraryIds")] public List<string>? LibraryIds { get; set; }
    }

    public sealed class LibraryBatch
    {
        [JsonPropertyName("items")] public List<LibraryItem> Items { get; set; } = new List<LibraryItem>();
        [JsonPropertyName("removed")] public List<int> Removed { get; set; } = new List<int>();
        [JsonPropertyName("generation")] public string? Generation { get; set; }
        [JsonPropertyName("final")] public bool Final { get; set; }
    }

    public sealed class LibraryResult
    {
        [JsonPropertyName("stored")] public int Stored { get; set; }
        [JsonPropertyName("removed")] public int Removed { get; set; }
        [JsonPropertyName("followed")] public int Followed { get; set; }
    }

    public sealed class RemoteWatch
    {
        [JsonPropertyName("tvdb_series_id")] public int TvdbSeriesId { get; set; }
        [JsonPropertyName("tvdb_episode_id")] public int TvdbEpisodeId { get; set; }
        [JsonPropertyName("season")] public int Season { get; set; }
        [JsonPropertyName("episode")] public int Episode { get; set; }
        [JsonPropertyName("watched_at")] public DateTimeOffset WatchedAt { get; set; }
    }

    public sealed class WatchesPage
    {
        [JsonPropertyName("watches")] public List<RemoteWatch> Watches { get; set; } = new List<RemoteWatch>();
        [JsonPropertyName("cursor")] public string Cursor { get; set; } = "";
        [JsonPropertyName("more")] public bool More { get; set; }
    }

    internal sealed class TokenPair
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    }

    internal sealed class ErrorBody
    {
        [JsonPropertyName("detail")] public object? Detail { get; set; }
    }
}
