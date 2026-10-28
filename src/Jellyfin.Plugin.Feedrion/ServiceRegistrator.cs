using System;
using System.IO;
using System.Net.Http;
using Feedrion.MediaServer.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Feedrion
{
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<JellyfinAdapter>();
            serviceCollection.AddSingleton(provider =>
            {
                var plugin = Plugin.Instance ?? throw new InvalidOperationException("Feedrion plugin not loaded.");
                var logger = provider.GetRequiredService<ILogger<SyncEngine>>();
                var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Feedrion-Jellyfin/" + typeof(Plugin).Assembly.GetName().Version?.ToString(3));
                return new SyncEngine(
                    provider.GetRequiredService<JellyfinAdapter>(),
                    new LinkStore(Path.Combine(plugin.DataFolderPath, "links")),
                    http,
                    () => plugin.Configuration.ApiBaseUrl,
                    (message, error) => logger.LogWarning(error, "{Message}", message))
                {
                    IgnoredSeries = () => plugin.Configuration.IgnoredTvdbSeriesIds ?? Array.Empty<int>(),
                };
            });
            serviceCollection.AddHostedService<FeedrionSyncService>();
        }
    }
}
