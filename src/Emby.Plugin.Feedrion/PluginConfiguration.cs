using Feedrion.MediaServer.Core;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugin.Feedrion
{
    /// <summary>The server-wide settings. Each user's link lives in the plugin's
    /// data folder, never in this file.</summary>
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        public string ApiBaseUrl { get; set; } = FeedrionClient.DefaultBaseUrl;

        /// <summary>Only ever a LINK in the reader's Feedrion app.</summary>
        public string PublicServerUrl { get; set; } = "";

        public int SyncIntervalMinutes { get; set; } = 15;
    }
}
