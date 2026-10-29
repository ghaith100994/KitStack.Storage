using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.S3.HealthChecks;
using KitStack.Storage.S3.Options;
using KitStack.Storage.S3.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.S3.Extensions;

public static class S3ServiceCollectionExtensions
{
    /// <summary>
    /// Register the S3 provider from the <c>S3</c> sub-section of the storage configuration
    /// (<c>Storage:S3</c>), plus a <c>kitstack_s3</c> health check.
    /// </summary>
    public static KitStackStorageBuilder AddS3(this KitStackStorageBuilder builder, Action<S3Options>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddS3StorageManager(builder.Configuration);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHealthChecks().AddCheck<S3HealthCheck>("kitstack_s3");
        return builder;
    }

    /// <summary>
    /// Create the configured buckets that have <c>EnsureBucketExists = true</c> (call once at startup).
    /// </summary>
    public static Task EnsureS3BucketsExistAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<S3FileStorageManager>().EnsureBucketsExistAsync(cancellationToken);
    }

    public static IServiceCollection AddS3StorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("S3");

        services.AddOptions<S3Options>()
                .Configure(section.Bind)
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.AddSingleton<S3HealthCheck>();
        services.AddSingleton<S3FileStorageManager>();
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<S3FileStorageManager>());
        services.AddSingleton<IS3PresignedUrlGenerator, S3PresignedUrlGenerator>();
        return services;
    }

    public static IServiceCollection AddS3StorageManager(this IServiceCollection services, S3Options options)
    {
        // Validate options instance immediately so errors surface during startup registration
        var results = new List<ValidationResult>();
        var ctx = new ValidationContext(options);
        if (!Validator.TryValidateObject(options, ctx, results, validateAllProperties: true))
        {
            throw new OptionsValidationException(nameof(S3Options), typeof(S3Options), results.Select(r => r.ErrorMessage ?? string.Empty));
        }

        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));

        services.AddSingleton<S3HealthCheck>();
        services.AddSingleton<S3FileStorageManager>();
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<S3FileStorageManager>());
        services.AddSingleton<IS3PresignedUrlGenerator, S3PresignedUrlGenerator>();
        return services;
    }
}
