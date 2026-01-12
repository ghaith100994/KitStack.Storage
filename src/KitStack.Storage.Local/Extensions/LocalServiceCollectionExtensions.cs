using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.Local.Extensions;

/// <summary>
/// Registration helpers for the Local provider.
/// Consumers can call AddLocalStorageManager to register LocalOptions and LocalFileStorageManager.
/// </summary>
public static class LocalServiceCollectionExtensions
{
    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Bind Storage:Local or a provided section
        var section = configuration.GetSection("Local");
        // Use the configuration binder to bind the section to LocalOptions via an Action<T>
        services.Configure<LocalOptions>(opts => section.Bind(opts));

        services.AddSingleton<LocalFileHealthCheck>();
        services.AddSingleton<IFileStorageManager, LocalFileStorageManager>();

        return services;
    }

    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, LocalOptions options)
    {
        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<LocalFileHealthCheck>();
        services.AddSingleton<IFileStorageManager, LocalFileStorageManager>();
        return services;
    }
}