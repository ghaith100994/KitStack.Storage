using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;

namespace KitStack.Storage.Local.Extensions;

/// <summary>
/// Registration helpers for the local storage provider.
/// This ensures that the local storage manager and dependencies are registered in DI.
/// </summary>
public static class LocalServiceCollectionExtensions
{
    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Local");

        // Bind LocalOptions from the provided configuration
        // Configure options so they are available via IOptionsMonitor<T>
        services.Configure<LocalOptions>(opts => section.Bind(opts));

        // Register manager and related services
        return services.AddLocalStorageManager();
    }
    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, bool isDefault = false)
    {
        // LocalOptions will be available via IOptionsMonitor<LocalOptions>
        // Add the local storage manager service
        services.AddSingleton<IFileStorageManager, LocalFileStorageManager>();
        services.AddSingleton<LocalFileStorageManager>();

        // Register a StorageProviderRegistration so runtime resolver can discover manager mapping
        var provider = new StorageProvider
        {
            Id = Guid.NewGuid(),
            Name = "local",
            ProviderType = StorageProviderType.Local,
            DisplayName = "Local Filesystem",
            IsDefault = isDefault,
            Options = null,
            OptionsType = typeof(LocalOptions).AssemblyQualifiedName,
            ManagerType = typeof(LocalFileStorageManager).AssemblyQualifiedName,
        };

        services.TryAddEnumerable(ServiceDescriptor.Singleton(new StorageProviderRegistration(provider, typeof(LocalFileStorageManager))));

        return services;
    }
}
