using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.Feedrion
{
    /// <summary>Feedrion for Emby — the same three flows as the Jellyfin plugin,
    /// over Emby's own API.</summary>
    public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static readonly Guid PluginId = new Guid("8c3e1d72-4a9b-4f0e-b6d2-71a5c9e30f14");

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

        /// <summary>The picture of the plugin in Emby's plugin list and catalogue.</summary>
        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage() =>
            GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".thumb.png")
            ?? throw new InvalidOperationException("thumb.png is not embedded.");

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "feedrion",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
            new PluginPageInfo
            {
                Name = "feedrionjs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js",
            },
        };
    }
}
