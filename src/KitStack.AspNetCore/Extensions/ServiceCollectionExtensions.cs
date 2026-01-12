using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using KitStack.Abstractions.Options;
using KitStack.Storage.Local.Extensions;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Fakes.Extensions;

namespace KitStack.AspNetCore.Extensions;

/// <summary>
/// Central registration surface for KitStack storage in ASP.NET Core apps.
/// - Binds StorageOptions from configuration
/// - Registers default providers (Local, Fake) when selected
/// - Registers corresponding health checks
/// - Binds Storage.Database options (for optional DB wiring)
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register KitStack storage services based on a configuration section (expects the Storage section).
    /// This method will:
    /// - Configure StorageOptions and Storage.Database (StorageDbOptions)
    /// - Register the provider selected by Storage:Provider (Local and Fake handled here)
    /// - Register provider health checks where available
    /// For cloud providers (Azure, Amazon, etc.) prefer calling their provider registration extensions
    /// (e.g. AddAzureBlobStorage) which the provider projects should expose.
    /// </summary>
    public static IServiceCollection AddKitStackStorage(this IServiceCollection services, IConfigurationSection storageSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storageSection);

        // Bind Storage options
        services.Configure<StorageOptions>(opts => storageSection.Bind(opts));

        // Bind Storage.Database section (optional)
        var dbSection = storageSection.GetSection("Database");
        services.Configure<StorageDbOptions>(opts => dbSection.Bind(opts));

        // decide which provider to register (simple switch)
        var provider = storageSection.GetValue<string>("Provider") ?? string.Empty;

        switch (provider.Trim().ToLowerInvariant())
        {
            case "local":
                // Local provider project exposes AddLocalStorageManager extension; use it
                services.AddLocalStorageManager(storageSection);
                // register health check for local provider
                services.AddHealthChecks().AddCheck<LocalFileHealthCheck>("local_file");
                break;

            case "fake":
                // In-memory fake used for tests/dev
                services.AddInMemoryFakeStorage();
                break;

            // For Azure/Amazon/other: call provider-specific registration extensions if available.
            // Example (if you add provider projects):
            // case "azure":
            //     services.AddAzureBlobStorage(storageSection.GetSection("Azure"));
            //     services.AddHealthChecks().AddCheck<AzureBlobStorageHealthCheck>("azure_blob");
            //     break;

            default:
                // No default provider registered here. Consumers can register provider implementations manually
                // (recommended for cloud providers or custom wiring).
                break;
        }

        return services;
    }

    /// <summary>
    /// Convenience overload that reads the "Storage" section from the configuration root.
    /// </summary>
    public static IServiceCollection AddKitStackStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddKitStackStorage(configuration.GetSection("Storage"));
    }
}