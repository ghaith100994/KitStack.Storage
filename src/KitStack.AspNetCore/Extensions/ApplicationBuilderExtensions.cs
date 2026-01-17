using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using KitStack.Storage.Local.Options;
using Microsoft.Extensions.Options;
using KitStack.Storage.Local.Services;
using Microsoft.Extensions.DependencyInjection;
using KitStack.AspNetCore.Middleware;

namespace KitStack.AspNetCore.Extensions;

/// <summary>
/// Small middleware helpers for storage consumption.
/// - For Local provider, configures static files serving from the configured Local path.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Configure application middleware related to storage.
    /// For Local provider this registers UseStaticFiles with a PhysicalFileProvider based on Storage:Local:Path.
    /// For other providers this method is a no-op by default.
    /// </summary>
    public static IApplicationBuilder UseKitStackStorage(this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        var storageSection = configuration.GetSection("Storage");
        var provider = storageSection.GetValue<string>("Provider") ?? string.Empty;

        if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            // Use a middleware that can dynamically adjust both the filesystem root and the request prefix
            app.UseMiddleware<DynamicStaticFileMiddleware>();
        }

        return app;
    }
}
