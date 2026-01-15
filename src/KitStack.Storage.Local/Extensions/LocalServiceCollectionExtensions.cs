using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KitStack.Storage.Local.Extensions;

/// <summary>
/// Registration helpers for the Local provider.
/// Consumers can call AddLocalStorageManager to register LocalOptions and LocalFileStorageManager.
/// </summary>
public static class LocalServiceCollectionExtensions
{
    /// <summary>
    /// Bind Storage:Local from configuration and register the local provider.
    /// This will create a StorageProvider instance (ProviderType = "Local") and register it in DI.
    /// </summary>
    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Local");
        var options = section.Get<LocalOptions>() ?? new LocalOptions();

        return services.AddLocalStorageManager(options);
    }

    /// <summary>
    /// Register local storage manager using the provided LocalOptions.
    /// Optionally provide a StorageProvider instance (code-first). If provider is null a reasonable default is created.
    /// The provider will be registered in DI and the provider registration will be added (so registries can pick it up).
    /// </summary>
    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, LocalOptions options, StorageProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Create a default StorageProvider if none provided
        if (provider == null)
        {
            provider = new StorageProvider
            {
                Id = Guid.NewGuid(),
                Name = "local",
                ProviderType = StorageProviderType.Local, // implicit string conversion
                DisplayName = "Local filesystem",
                IsDefault = true,
                Options = options,
                OptionsType = typeof(LocalOptions).AssemblyQualifiedName,
                ManagerType = typeof(LocalFileStorageManager).AssemblyQualifiedName
            };
        }
        else
        {
            // Ensure provider has Options info set for convenience
            provider.Options ??= options;
            provider.OptionsType ??= typeof(LocalOptions).AssemblyQualifiedName;
            provider.ManagerType ??= typeof(LocalFileStorageManager).AssemblyQualifiedName;
        }

        // Register the provider instance into DI so managers can obtain it via constructor injection
        services.TryAddSingleton(provider);

        // Also register the provider into the in-memory registration mechanism (optional, depends on Abstractions.Extensions.AddStorageProvider)
        // This call will register a StorageProviderRegistration so StorageProviderRegistry seeded from DI can pick it up.
        services.AddStorageProvider(provider, typeof(LocalFileStorageManager));

        // Register options in IOptions<T> form for components that still depend on it
        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));

        // Health check
        services.TryAddSingleton<LocalFileHealthCheck>();

        // Register the concrete manager and the IFileStorageManager mapping
        // Local manager is registered as singleton (keeps configuration state consistent); consumer can override if needed.
        services.TryAddSingleton<LocalFileStorageManager>();
        services.TryAddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<LocalFileStorageManager>());

        return services;
    }
}
