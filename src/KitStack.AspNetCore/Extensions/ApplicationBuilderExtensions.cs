using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace KitStack.AspNetCore.Extensions;

/// <summary>
/// Middleware helpers for storage consumption.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// For the Local provider, serve the storage directory as anonymous static files when
    /// <c>Storage:Local:ServeAnonymously</c> is <c>true</c>. Anyone who knows or guesses a path can then download
    /// the file, and encrypted files are served as ciphertext, so this is off by default since 0.2:
    /// use <see cref="FileEndpointRouteBuilderExtensions.MapKitStackStorageFiles"/> instead.
    /// For other providers, or when the flag is off, this method does nothing.
    /// </summary>
    public static IApplicationBuilder UseKitStackStorage(this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        var storageSection = configuration.GetSection("Storage");
        var provider = storageSection.GetValue<string>("Provider") ?? string.Empty;
        var localSection = storageSection.GetSection("Local");

        if (!provider.Equals("Local", StringComparison.OrdinalIgnoreCase) || !localSection.GetValue<bool>("ServeAnonymously"))
            return app;

        var path = localSection.GetValue<string>("Path");
        if (string.IsNullOrWhiteSpace(path))
            path = "Files";
        var basePath = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
        Directory.CreateDirectory(basePath);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(basePath),
            RequestPath = new PathString("/" + path.Replace('\\', '/').Trim('/')),
        });

        return app;
    }
}
