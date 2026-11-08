using System.ComponentModel.DataAnnotations;
using KitStack.Storage.Abstractions.DependencyInjection;
using KitStack.Storage.Abstractions.Extensions;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
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
        builder.AddS3Provider().AddDefaultStorageInstance(S3StorageProviderFactory.Type);
        builder.Services.AddS3StorageManager(builder.Configuration);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHealthChecks().AddCheck<S3HealthCheck>("kitstack_s3");
        return builder;
    }

    /// <summary>
    /// Register a named S3 instance (another bucket, prefix or account), bound from <c>Storage:Instances:{name}:S3</c> and
    /// <paramref name="configure"/>, with storage overrides from <c>Storage:Instances:{name}</c>. Several named instances
    /// coexist; resolve them with <c>IFileStorageManagerProvider.Get(name)</c> or <c>[FromKeyedServices(name)]</c>.
    /// </summary>
    public static KitStackStorageBuilder AddS3(this KitStackStorageBuilder builder, string name, Action<S3Options>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (StorageInstance.IsDefault(name))
            return builder.AddS3(configure);
        return builder.AddS3Provider().AddNamedStorageInstance(name, S3StorageProviderFactory.Type, "S3", configure);
    }

    /// <summary>Register the S3 provider factory so runtime instances with <c>ProviderType = "S3"</c> can be built.</summary>
    public static KitStackStorageBuilder AddS3Provider(this KitStackStorageBuilder builder)
        => builder.AddStorageProviderFactory<S3StorageProviderFactory>();

    /// <summary>Create the buckets of the instance <paramref name="storageName"/> that have <c>EnsureBucketExists = true</c>.</summary>
    public static Task EnsureS3BucketsExistAsync(this IServiceProvider services, string storageName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var manager = services.GetRequiredService<IFileStorageManagerProvider>().Get(storageName).Unwrap<S3FileStorageManager>()
            ?? throw new InvalidOperationException($"Storage instance '{storageName}' is not an S3 instance.");
        return manager.EnsureBucketsExistAsync(cancellationToken);
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
        services.AddSingleton(CreateManager);
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
        services.AddSingleton(CreateManager);
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<S3FileStorageManager>());
        services.AddSingleton<IS3PresignedUrlGenerator, S3PresignedUrlGenerator>();
        return services;
    }

    private static S3FileStorageManager CreateManager(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<S3Options>>();
        return new S3FileStorageManager(
            options,
            S3StorageProviderFactory.ClientFactory(sp, options.Value),
            sp.GetService<IOptions<KitStack.Storage.Abstractions.Options.StorageOptions>>(),
            sp.GetService<KitStack.Storage.Abstractions.Security.IFileContentEncryptor>());
    }
}
