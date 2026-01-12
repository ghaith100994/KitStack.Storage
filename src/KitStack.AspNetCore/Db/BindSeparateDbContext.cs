using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.AspNetCore.Db;

/// <summary>
/// Helper placeholders for registering a separate database/context used by the Storage module.
/// This project intentionally performs only lightweight option binding. Actual DB wiring (EF Core, MongoDB, etc.)
/// should be implemented in the infrastructure layer or by calling provider-specific registration code
/// to avoid bringing heavy SDK dependencies into this package.
/// </summary>
public static class BindSeparateDbContextExtensions
{
    /// <summary>
    /// Bind TOptions from configuration (Storage:Database) and return IServiceCollection.
    /// Implementations that need to register a concrete DbContext or MongoDB mappings should be placed in
    /// the infrastructure project (where DB SDK dependencies can be added).
    /// </summary>
    public static IServiceCollection BindSeparateDbContextOptions<TContext, TOptions>(this IServiceCollection services, IConfiguration configuration)
        where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind Storage.Database into TOptions (consumer should provide Storage.Database in configuration)
        var dbSection = configuration.GetSection("Storage").GetSection("Database");
        // Bind the Storage:Database section into TOptions using the configuration binder
        services.Configure<TOptions>(opts => dbSection.Bind(opts));

        // NOTE:
        // - Do not attempt to register EF Core/Mongo context here to avoid heavy package coupling.
        // - In your Infrastructure project implement a method that reads the bound options and registers the real DB:
        //   Example: services.AddStorageMongoDb(configuration) or services.AddStorageRelationalDb(configuration)
        //
        // For convenience, consumers can still call this placeholder to ensure options are bound.
        return services;
    }
}