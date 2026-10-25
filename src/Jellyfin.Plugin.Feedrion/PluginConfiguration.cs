using Feedrion.MediaServer.Core;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Feedrion
{
    /// <summary>The server-wide settings. Each user's link (tokens, progress) lives
    /// in the plugin's data folder, never in this file.</summary>
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>The Feedrion API root; https only.</summary>
        public string ApiBaseUrl { get; set; } = FeedrionClient.DefaultBaseUrl;

        /// <summary>The address readers open this server at. Only ever a LINK in
        /// their Feedrion app; Feedrion never connects to it.</summary>
        public string PublicServerUrl { get; set; } = "";

        /// <summary>Minutes between two passes when nothing happens.</summary>
        public int SyncIntervalMinutes { get; set; } = 15;
    }
}
