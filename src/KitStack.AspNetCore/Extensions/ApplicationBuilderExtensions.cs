using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using KitStack.Abstractions.Options;
using KitStack.Storage.Local.Options;

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
            var localSection = storageSection.GetSection("Local");
            var localOptions = localSection.Get<LocalOptions>() ?? new LocalOptions();

            var basePath = Path.IsPathRooted(localOptions.Path)
                ? localOptions.Path
                : Path.Combine(Directory.GetCurrentDirectory(), localOptions.Path);

            if (!Directory.Exists(basePath) && localOptions.EnsureBasePathExists)
            {
                Directory.CreateDirectory(basePath);
            }

            var providerFs = new PhysicalFileProvider(basePath);
            var requestPath = new PathString("/" + localOptions.Path.Trim('/'));

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = providerFs,
                RequestPath = requestPath
            });
        }

        return app;
    }
}