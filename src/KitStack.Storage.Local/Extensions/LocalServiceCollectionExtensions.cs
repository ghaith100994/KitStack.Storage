using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.Local.Extensions;

/// <summary>
/// Registration helpers for the Local provider.
/// </summary>
public static class LocalServiceCollectionExtensions
{
    /// <summary>
    /// Register the Local provider from the <c>Local</c> sub-section of the storage configuration
    /// (<c>Storage:Local</c>), plus a <c>kitstack_local</c> health check.
    /// </summary>
    public static KitStackStorageBuilder AddLocal(this KitStackStorageBuilder builder, Action<LocalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLocalStorageManager(builder.Configuration);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHealthChecks().AddCheck<LocalFileHealthCheck>("kitstack_local");
        return builder;
    }

    /// <summary>
    /// Register LocalOptions (bound from the <c>Local</c> sub-section of <paramref name="configuration"/>)
    /// and <see cref="LocalFileStorageManager"/>.
    /// </summary>
    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Local");
        services.Configure<LocalOptions>(section.Bind);

        services.AddSingleton<LocalFileHealthCheck>();
        services.AddSingleton<IFileStorageManager, LocalFileStorageManager>();

        return services;
    }

    /// <summary>Register the Local provider with an options instance.</summary>
    public static IServiceCollection AddLocalStorageManager(this IServiceCollection services, LocalOptions options)
    {
        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<LocalFileHealthCheck>();
        services.AddSingleton<IFileStorageManager, LocalFileStorageManager>();
        return services;
    }
}
