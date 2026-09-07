using Jellyfin.Plugin.TagTurbo.Middleware;
using Jellyfin.Plugin.TagTurbo.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TagTurbo;

/// <summary>
/// Registers plugin services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IQualityDetectionService, QualityDetectionService>();
        serviceCollection.AddSingleton<IImageOverlayService, ImageOverlayService>();
        serviceCollection.AddSingleton<IImageCacheService, ImageCacheService>();

        // Register middleware via IStartupFilter to intercept image requests for ALL clients
        serviceCollection.AddSingleton<IStartupFilter, TagTurboStartupFilter>();
    }
}
