using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using KitStack.Storage.Local.Options;
using KitStack.Storage.S3.Options;
using KitStack.Storage.S3.Helpers;
using KitStack.Storage.S3.Services;

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

    /// <summary>
    /// Optionally ensure S3 buckets exist during app startup when using the S3 provider.
    /// This will call the S3BucketHelper using a delegate that resolves clients from S3FileStorageManager.
    /// </summary>
    public static async Task<IApplicationBuilder> UseKitStackStorageEnsureBucketsAsync(this IApplicationBuilder app, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        var storageSection = configuration.GetSection("Storage");
        var provider = storageSection.GetValue<string>("Provider") ?? string.Empty;

        if (provider.Equals("S3", StringComparison.OrdinalIgnoreCase))
        {
            var s3Options = configuration.GetSection("Storage:S3").Get<S3Options>();
            if (s3Options != null)
            {
                var s3Manager = app.ApplicationServices.GetService(typeof(S3FileStorageManager)) as S3FileStorageManager;
                if (s3Manager != null)
                {
                    await S3BucketHelper.EnsureBucketsExistAsync(s3Options, s3Manager.ResolveClientAndBucket, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return app;
    }
}
