using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Feedrion.MediaServer.Core
{
    /// <summary>One server user as the configuration page shows it: no token,
    /// ever — only whether it is linked and how the last pass went.</summary>
    public sealed class LinkView
    {
        [JsonPropertyName("userId")] public string UserId { get; set; } = "";
        [JsonPropertyName("userName")] public string? UserName { get; set; }
        [JsonPropertyName("linked")] public bool Linked { get; set; }
        [JsonPropertyName("feedrionName")] public string? FeedrionName { get; set; }
        [JsonPropertyName("premium")] public bool? Premium { get; set; }
        [JsonPropertyName("pricingUrl")] public string? PricingUrl { get; set; }
        [JsonPropertyName("lastSyncAt")] public DateTimeOffset? LastSyncAt { get; set; }
        [JsonPropertyName("lastError")] public string? LastError { get; set; }
        [JsonPropertyName("pendingEvents")] public int PendingEvents { get; set; }
        [JsonPropertyName("pairingCode")] public string? PairingCode { get; set; }
        [JsonPropertyName("pairingUrl")] public string? PairingUrl { get; set; }
        [JsonPropertyName("pairingExpiresAt")] public DateTimeOffset? PairingExpiresAt { get; set; }
        [JsonPropertyName("pairingError")] public string? PairingError { get; set; }
        [JsonPropertyName("addNewShows")] public bool AddNewShows { get; set; }
        [JsonPropertyName("libraryIds")] public List<string>? LibraryIds { get; set; }
        [JsonPropertyName("neverUnwatch")] public bool NeverUnwatch { get; set; }

        public static LinkView From(string userId, string? userName, LinkState? link, PendingLink? pending) => new LinkView
        {
            UserId = userId,
            UserName = userName,
            Linked = link?.IsLinked == true,
            FeedrionName = link?.FeedrionName,
            Premium = link?.Premium,
            PricingUrl = link?.PricingUrl,
            LastSyncAt = link?.LastSyncAt,
            LastError = link?.LastError,
            PendingEvents = link?.PendingEvents.Count ?? 0,
            PairingCode = pending?.UserCode,
            PairingUrl = pending?.VerificationUrl,
            PairingExpiresAt = pending?.ExpiresAt,
            PairingError = pending?.Error,
            AddNewShows = link?.AddNewShows ?? false,
            LibraryIds = link?.LibraryIds,
            NeverUnwatch = link?.NeverUnwatch ?? false,
        };
    }
}
