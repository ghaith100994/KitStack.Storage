using KitStack.Storage.Abstractions.DependencyInjection;
using KitStack.Storage.Abstractions.Extensions;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Services;
using KitStack.Storage.Fakes.Stores;
using KitStack.Storage.Fakes.Contracts;
using KitStack.Storage.Fakes.Options;
using KitStack.Storage.Fakes.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Fakes.Extensions;

/// <summary>
/// DI helpers for registering the fake storage provider in test or dev environments.
/// </summary>
public static class FakeServiceCollectionExtensions
{
    /// <summary>
    /// Register the in-memory fake provider, bound from the <c>Fake</c> sub-section of the storage configuration.
    /// </summary>
    public static KitStackStorageBuilder AddFake(this KitStackStorageBuilder builder, Action<FakeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddFakeProvider().AddDefaultStorageInstance(FakeStorageProviderFactory.Type);
        builder.Services.AddInMemoryFakeStorage(builder.Configuration.GetSection("Fake"));
        if (configure is not null)
            builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Register a named in-memory instance with its own isolated store. Its <see cref="IFakeFileStore"/> is available as a
    /// keyed service: <c>sp.GetRequiredKeyedService&lt;IFakeFileStore&gt;(name)</c>.
    /// </summary>
    public static KitStackStorageBuilder AddFake(this KitStackStorageBuilder builder, string name, Action<FakeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (StorageInstance.IsDefault(name))
            return builder.AddFake(configure);
        builder.AddFakeProvider().AddNamedStorageInstance(name, FakeStorageProviderFactory.Type, "Fake", configure);
        builder.Services.AddKeyedSingleton<IFakeFileStore>(name.Trim(), (sp, _) =>
            sp.GetRequiredService<IFileStorageManagerProvider>().Get(name).Unwrap<InMemoryFileStorageManager>()
            ?? throw new InvalidOperationException($"Storage instance '{name}' is not an in-memory instance."));
        return builder;
    }

    /// <summary>Register the fake provider factory so runtime instances with <c>ProviderType = "Fake"</c> can be built in memory.</summary>
    public static KitStackStorageBuilder AddFakeProvider(this KitStackStorageBuilder builder)
        => builder.AddStorageProviderFactory<FakeStorageProviderFactory>();

    /// <summary>
    /// Use an <see cref="InMemoryFileEntryStore"/> as the <see cref="IFileEntryStore"/> (unit tests, local development).
    /// </summary>
    /// <param name="builder">The storage builder; add the providers first.</param>
    /// <param name="trackUploads">Record every file created through the storage managers (default).</param>
    public static KitStackStorageBuilder AddInMemoryStore(this KitStackStorageBuilder builder, bool trackUploads = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<InMemoryFileEntryStore>();
        builder.Services.TryAddSingleton<IFileEntryStore>(sp => sp.GetRequiredService<InMemoryFileEntryStore>());
        builder.Services.TryAddScoped<FileOrphanSweeper>();
        if (trackUploads)
            builder.AddFileEntryTracking();
        return builder;
    }

    /// <summary>
    /// Register the in-memory fake storage manager and optional configuration.
    /// Returns IServiceCollection for chaining.
    /// </summary>
    public static IServiceCollection AddInMemoryFakeStorage(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration != null)
        {
            // Bind the provided configuration to FakeOptions
            services.Configure<FakeOptions>(opts => configuration.Bind(opts));
        }
        else
        {
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new FakeOptions()));
        }

        services.AddSingleton<InMemoryFileStorageManager>();
        // register both IFileStorageManager and IFakeFileStore to the same instance
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<InMemoryFileStorageManager>());
        services.AddSingleton<IFakeFileStore>(sp => sp.GetRequiredService<InMemoryFileStorageManager>());

        return services;
    }

    public static IServiceCollection AddInMemoryFakeStorage(this IServiceCollection services, Action<FakeOptions> configure)
    {
        var options = new FakeOptions();
        configure?.Invoke(options);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<InMemoryFileStorageManager>();
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<InMemoryFileStorageManager>());
        services.AddSingleton<IFakeFileStore>(sp => sp.GetRequiredService<InMemoryFileStorageManager>());
        return services;
    }
}