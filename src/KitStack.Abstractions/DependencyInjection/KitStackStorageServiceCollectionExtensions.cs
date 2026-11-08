using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Hosting;
using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KitStack.Abstractions.DependencyInjection;

/// <summary>
/// Entry point for registering KitStack storage.
/// </summary>
public static class KitStackStorageServiceCollectionExtensions
{
    internal const string InnerManagerKey = "KitStack.Storage.Inner";

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
    /// has been added: it wraps the registered <see cref="IFileStorageManager"/> in a scoped decorator, and managers
    /// returned by <see cref="IFileStorageManagerProvider"/> (named and runtime instances) are tracked as well.
    /// </summary>
    /// <exception cref="InvalidOperationException">No provider, provider factory or instance source is registered yet.</exception>
    public static KitStackStorageBuilder AddFileEntryTracking(this KitStackStorageBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // The decorator is registered through a factory, so detect a previous call with a marker:
        // wrapping twice would make the inner registration resolve to the decorator itself.
        if (services.Any(d => d.ServiceType == typeof(FileEntryTrackingMarker)))
            return builder;

        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IFileStorageManager) && !d.IsKeyedService);
        if (descriptor is null)
        {
            // Applications that only use named or runtime instances have no unkeyed manager to wrap.
            var hasInstances = services.Any(d => d.ServiceType == typeof(IStorageProviderFactory)
                                                 || d.ServiceType == typeof(StaticStorageInstance)
                                                 || d.ServiceType == typeof(IStorageInstanceSource));
            if (!hasInstances)
                throw new InvalidOperationException("Add a storage provider (for example AddLocal()) before enabling file entry tracking.");
        }

        services.AddSingleton<FileEntryTrackingMarker>();
        if (descriptor is not null)
        {
            services.Remove(descriptor);
            services.Add(ToKeyed(descriptor, InnerManagerKey));
            services.AddScoped<IFileStorageManager>(sp => new FileEntryTrackingStorageManager(
                sp.GetRequiredKeyedService<IFileStorageManager>(InnerManagerKey),
                sp.GetRequiredService<IFileEntryStore>(),
                sp.GetService<IFileStorageManagerProvider>()));
        }

        services.TryAddScoped<FileOrphanSweeper>();
        return builder;
    }

    /// <summary>
    /// Register an <see cref="IStorageInstanceSource"/> that defines storage instances at runtime (for example rows of an
    /// administration table). Instances are built on first use by the provider factory of their <c>ProviderType</c>
    /// (register them with <c>AddLocalProvider()</c>, <c>AddS3Provider()</c>, ...), cached, and rebuilt when their
    /// <c>Version</c> changes. Resolve them through <see cref="IFileStorageManagerProvider"/>.
    /// </summary>
    public static KitStackStorageBuilder AddStorageInstanceSource<TSource>(this KitStackStorageBuilder builder, Action<StorageInstanceCacheOptions>? configure = null)
        where TSource : class, IStorageInstanceSource
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.RemoveAll<IStorageInstanceSource>();
        builder.Services.AddSingleton<IStorageInstanceSource, TSource>();
        if (configure is not null)
            builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>Register an <see cref="IStorageInstanceSource"/> instance; see <see cref="AddStorageInstanceSource{TSource}"/>.</summary>
    public static KitStackStorageBuilder AddStorageInstanceSource(this KitStackStorageBuilder builder, IStorageInstanceSource source, Action<StorageInstanceCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        builder.Services.RemoveAll<IStorageInstanceSource>();
        builder.Services.AddSingleton(source);
        if (configure is not null)
            builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>Replace the default <see cref="IStorageSecretResolver"/> (which reads <c>StorageSecrets:{reference}</c> from configuration).</summary>
    public static KitStackStorageBuilder AddStorageSecretResolver<TResolver>(this KitStackStorageBuilder builder)
        where TResolver : class, IStorageSecretResolver
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.RemoveAll<IStorageSecretResolver>();
        builder.Services.AddSingleton<IStorageSecretResolver, TResolver>();
        return builder;
    }

    /// <summary>
    /// Override the storage options (upload policy, encryption keys) of a statically registered named instance, on top
    /// of what is bound from <c>Storage:Instances:{name}</c>.
    /// </summary>
    public static KitStackStorageBuilder ConfigureInstance(this KitStackStorageBuilder builder, string name, Action<StorageOptionsOverrides> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.Configure(StorageInstance.Validate(name), configure);
        return builder;
    }

    /// <summary>Register a provider factory (used by provider packages' <c>AddXxxProvider()</c>). Registering the same type twice is a no-op.</summary>
    public static KitStackStorageBuilder AddStorageProviderFactory<TFactory>(this KitStackStorageBuilder builder)
        where TFactory : class, IStorageProviderFactory
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IStorageProviderFactory, TFactory>());
        return builder;
    }

    /// <summary>
    /// Register a statically configured named instance (used by provider packages' <c>AddXxx(name, ...)</c>):
    /// <typeparamref name="TOptions"/> named <paramref name="name"/> bound from <c>Storage:Instances:{name}:{sectionName}</c>
    /// and <paramref name="configure"/>, storage overrides from <c>Storage:Instances:{name}</c>, and a keyed
    /// <see cref="IFileStorageManager"/> (<c>[FromKeyedServices(name)]</c>) resolved through <see cref="IFileStorageManagerProvider"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">An instance with that name is already registered.</exception>
    public static KitStackStorageBuilder AddNamedStorageInstance<TOptions>(
        this KitStackStorageBuilder builder, string name, string providerType, string sectionName, Action<TOptions>? configure,
        Func<TOptions, bool>? validate = null)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        name = StorageInstance.Validate(name);
        if (StorageInstance.IsDefault(name))
            throw new ArgumentException("Register the default instance with the unnamed overload (for example AddS3()).", nameof(name));
        if (builder.Services.Any(d => d.ServiceType == typeof(StaticStorageInstance) && d.ImplementationInstance is StaticStorageInstance s && StorageInstance.AreSame(s.Name, name)))
            throw new InvalidOperationException($"A storage instance named '{name}' is already registered.");

        var instanceSection = builder.Configuration.GetSection($"Instances:{name}");
        var options = builder.Services.AddOptions<TOptions>(name).Configure(instanceSection.GetSection(sectionName).Bind);
        if (configure is not null)
            options.Configure(configure);
        if (validate is not null)
            options.Validate(validate, $"Invalid options for storage instance '{name}'.");

        builder.Services.Configure<StorageOptionsOverrides>(name, instanceSection.Bind);
        builder.Services.AddSingleton(new StaticStorageInstance(name, providerType, name));
        builder.Services.AddKeyedSingleton<IFileStorageManager>(name, (sp, _) => sp.GetRequiredService<IFileStorageManagerProvider>().Get(name));
        return builder;
    }

    /// <summary>
    /// Record that the unnamed (default) registration uses <paramref name="providerType"/>, so the default instance has
    /// capabilities and health probes. Called by provider packages' unnamed <c>AddXxx()</c>.
    /// </summary>
    public static KitStackStorageBuilder AddDefaultStorageInstance(this KitStackStorageBuilder builder, string providerType)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton(new StaticStorageInstance(StorageInstance.Default, providerType, Microsoft.Extensions.Options.Options.DefaultName));
        return builder;
    }

    /// <summary>
    /// Register one health check per statically registered instance (<c>kitstack_storage_{name}</c>), using each
    /// provider's health-check logic through <see cref="IStorageHealthProbe"/>. The provider-specific checks of the default
    /// instance (<c>kitstack_s3</c>, ...) are kept. Call it after the instances are registered.
    /// </summary>
    public static KitStackStorageBuilder AddKitStackStorageHealthChecks(this KitStackStorageBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var names = builder.Services
            .Select(d => d.ImplementationInstance)
            .OfType<StaticStorageInstance>()
            .Select(s => s.Name)
            .Distinct(StorageInstance.Comparer)
            .ToList();
        var health = builder.Services.AddHealthChecks();
        foreach (var name in names)
        {
            health.Add(new HealthCheckRegistration(
                $"kitstack_storage_{name}",
                sp => new StorageInstanceHealthCheck(sp.GetRequiredService<IStorageHealthProbe>(), name),
                failureStatus: null,
                tags: tags));
        }

        return builder;
    }

    /// <summary>
    /// Periodically delete orphaned files (older than <see cref="OrphanSweeperOptions.MinAge"/> and not linked to
    /// any entity) using the registered <see cref="IFileEntryStore"/>. Opt-in: only enable it once every place
    /// that keeps a file records a link (<c>StoredFileRef</c> with the EF interceptor, <c>CreateAsync(entity, ...)</c>
    /// or <see cref="IFileEntryStore.LinkAsync"/>); otherwise files that are still used get deleted.
    /// Options bind from <c>Storage:OrphanSweeper</c>.
    /// </summary>
    public static KitStackStorageBuilder AddOrphanSweeper(this KitStackStorageBuilder builder, Action<OrphanSweeperOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var section = builder.Configuration.GetSection("OrphanSweeper");
        builder.Services.AddOptions<OrphanSweeperOptions>()
            .Configure(section.Bind)
            .Validate(o => o.Interval > TimeSpan.Zero && o.MinAge >= TimeSpan.Zero && o.BatchSize > 0, "Invalid Storage:OrphanSweeper options.")
            .ValidateOnStart();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.TryAddScoped<FileOrphanSweeper>();
        builder.Services.AddHostedService<FileOrphanSweeperHostedService>();
        return builder;
    }

    private static void AddCore(IServiceCollection services)
    {
        services.AddOptions();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddOptions<StorageOptions>().ValidateOnStart();

        // Instances (0.3): the provider is always available; static and runtime instances register into it.
        services.AddOptions<StorageInstanceCacheOptions>();
        services.TryAddSingleton<IStorageSecretResolver>(sp => new ConfigurationStorageSecretResolver(sp));
        services.TryAddSingleton(sp => new StorageInstanceResolver(
            sp, sp.GetServices<IStorageProviderFactory>(), sp.GetRequiredService<IStorageSecretResolver>(), sp.GetRequiredService<IOptions<StorageOptions>>()));
        services.TryAddSingleton(sp => new FileStorageManagerProvider(
            sp, sp.GetServices<StaticStorageInstance>(), sp.GetRequiredService<StorageInstanceResolver>(),
            sp.GetRequiredService<IOptions<StorageInstanceCacheOptions>>(), sp.GetService<ILogger<FileStorageManagerProvider>>()));
        services.TryAddSingleton<IFileStorageManagerProvider>(sp => sp.GetRequiredService<FileStorageManagerProvider>());
        services.TryAddSingleton<IStorageInstanceValidator>(sp => new StorageInstanceValidator(sp.GetRequiredService<StorageInstanceResolver>()));
        services.TryAddSingleton<IStorageHealthProbe>(sp => new StorageHealthProbe(sp.GetRequiredService<FileStorageManagerProvider>()));
        services.TryAddSingleton<IFileTransferService>(sp => new FileTransferService(
            sp.GetRequiredService<IFileStorageManagerProvider>(), sp, sp.GetService<ILogger<FileTransferService>>()));
        services.TryAddSingleton<IFileVersionService>(sp => new FileVersionService(sp.GetRequiredService<IFileStorageManagerProvider>(), sp));
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

/// <summary>Marks that file entry tracking is enabled.</summary>
internal sealed class FileEntryTrackingMarker;
