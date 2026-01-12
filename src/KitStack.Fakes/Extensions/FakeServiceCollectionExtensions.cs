using KitStack.Abstractions.Interfaces;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Options;
using KitStack.Fakes.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Fakes.Extensions;

/// <summary>
/// DI helpers for registering the fake storage provider in test or dev environments.
/// </summary>
public static class FakeServiceCollectionExtensions
{
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