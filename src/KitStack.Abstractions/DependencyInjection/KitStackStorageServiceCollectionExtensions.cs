using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Abstractions.DependencyInjection;

/// <summary>
/// Entry point for registering KitStack storage.
/// </summary>
public static class KitStackStorageServiceCollectionExtensions
{
    private const string InnerManagerKey = "KitStack.Storage.Inner";

    /// <summary>
    /// Register the shared storage options (upload policy, encryption) from the <c>Storage</c> section of
    /// <paramref name="configuration"/> and return a builder to add a provider to.
    /// </summary>
    public static KitStackStorageBuilder AddKitStackStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddKitStackStorage(configuration.GetSection("Storage"));
    }

    /// <summary>
    /// Register the shared storage options from <paramref name="storageSection"/> (the <c>Storage</c> section)
    /// and return a builder to add a provider to.
    /// </summary>
    public static KitStackStorageBuilder AddKitStackStorage(this IServiceCollection services, IConfigurationSection storageSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storageSection);

        services.Configure<StorageOptions>(storageSection.Bind);
        services.Configure<StorageDbOptions>(storageSection.GetSection("Database").Bind);
        AddCore(services);
        return new KitStackStorageBuilder(services, storageSection);
    }

    /// <summary>Register KitStack storage configured in code.</summary>
    public static KitStackStorageBuilder AddKitStackStorage(this IServiceCollection services, Action<StorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null)
            services.Configure(configure);
        AddCore(services);
        return new KitStackStorageBuilder(services, new ConfigurationBuilder().Build());
    }

    /// <summary>Further configure the shared storage options.</summary>
    public static KitStackStorageBuilder Configure(this KitStackStorageBuilder builder, Action<StorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Record every file created through <see cref="IFileStorageManager"/> in the registered
    /// <see cref="IFileEntryStore"/>, and mark it deleted when it is deleted. Call this after a provider
    /// has been added: it wraps the registered <see cref="IFileStorageManager"/> in a scoped decorator.
    /// </summary>
    /// <exception cref="InvalidOperationException">No <see cref="IFileStorageManager"/> is registered yet.</exception>
    public static KitStackStorageBuilder AddFileEntryTracking(this KitStackStorageBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IFileStorageManager) && !d.IsKeyedService)
            ?? throw new InvalidOperationException("Add a storage provider (for example AddLocal()) before enabling file entry tracking.");
        if (descriptor.ImplementationType == typeof(FileEntryTrackingStorageManager))
            return builder;

        services.Remove(descriptor);
        services.Add(ToKeyed(descriptor, InnerManagerKey));
        services.AddScoped<IFileStorageManager>(sp => new FileEntryTrackingStorageManager(
            sp.GetRequiredKeyedService<IFileStorageManager>(InnerManagerKey),
            sp.GetRequiredService<IFileEntryStore>()));
        services.AddScoped<FileOrphanSweeper>();
        return builder;
    }

    private static void AddCore(IServiceCollection services)
    {
        services.AddOptions();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddOptions<StorageOptions>().ValidateOnStart();
    }

    private static ServiceDescriptor ToKeyed(ServiceDescriptor descriptor, object key)
    {
        if (descriptor.ImplementationInstance is { } instance)
            return new ServiceDescriptor(descriptor.ServiceType, key, instance);
        if (descriptor.ImplementationFactory is { } factory)
            return new ServiceDescriptor(descriptor.ServiceType, key, (sp, _) => factory(sp), descriptor.Lifetime);
        return new ServiceDescriptor(descriptor.ServiceType, key, descriptor.ImplementationType!, descriptor.Lifetime);
    }

    private sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
    {
        public ValidateOptionsResult Validate(string? name, StorageOptions options)
        {
            try
            {
                _ = AesGcmFileContentEncryptor.FromOptions(options);
            }
            catch (StorageConfigurationException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }

            if (options.Upload is { MaxBytes: < 0 })
                return ValidateOptionsResult.Fail("Storage:Upload:MaxBytes must be 0 (unlimited) or positive.");

            return ValidateOptionsResult.Success;
        }
    }
}
