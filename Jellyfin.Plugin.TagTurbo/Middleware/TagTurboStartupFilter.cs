using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.TagTurbo.Middleware;

/// <summary>
/// Startup filter that registers the image overlay middleware into the ASP.NET Core pipeline.
/// </summary>
public class TagTurboStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<ImageOverlayMiddleware>();
            next(app);
        };
    }
}
