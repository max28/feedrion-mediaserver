using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Feedrion
{
    /// <summary>Feedrion for Jellyfin: watched episodes into the Feedrion « Séries »
    /// extension, the server's library as « disponible sur ton serveur », and
    /// the watches made elsewhere back onto the server.</summary>
    public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static readonly Guid PluginId = new Guid("5f0c2a8e-6b1d-4c7e-9a3f-2d8e1b4c6a90");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public override string Name => "Feedrion";

        public override Guid Id => PluginId;

        public override string Description =>
            "Sync watched episodes with Feedrion, show which episodes this server holds, and mark here what you watched elsewhere.";

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        };
    }
}
