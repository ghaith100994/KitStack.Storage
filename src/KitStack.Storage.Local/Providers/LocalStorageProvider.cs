using System.IO;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.Local.Providers;

/// <summary>
/// Strongly typed local storage provider definition. Encapsulates option wiring and manager registration.
/// </summary>
public sealed class LocalStorageProvider : StorageProvider
{
    private readonly LocalOptions _options;

    public LocalStorageProvider(string id, Action<LocalOptions>? configure = null)
        : this(id, ConfigureOptions(configure))
    {
    }

    public LocalStorageProvider(string id, LocalOptions options)
        : base(id)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.ImageProcessing ??= new ImageProcessingOptions();
        BasePath = ResolveBasePath(_options.Path);

        if (_options.EnsureBasePathExists)
            EnsureBasePath();
    }

    public LocalOptions Options => _options;

    /// <summary>
    /// Absolute base path resolved from the provider options.
    /// </summary>
    public string BasePath { get; }

    /// <summary>
    /// Ensures the base path exists on disk and returns it.
    /// </summary>
    public string EnsureBasePath()
    {
        if (!Directory.Exists(BasePath))
            Directory.CreateDirectory(BasePath);

        return BasePath;
    }

    private static LocalOptions ConfigureOptions(Action<LocalOptions>? configure)
    {
        var options = new LocalOptions();
        configure?.Invoke(options);
        return options;
    }

    private static string ResolveBasePath(string? configuredPath)
    {
        var target = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "Files")
            : configuredPath;

        return Path.IsPathRooted(target)
            ? target
            : Path.Combine(Directory.GetCurrentDirectory(), target);
    }

    public override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => this);
        services.AddSingleton<StorageProvider>(sp => sp.GetRequiredService<LocalStorageProvider>());
        services.AddSingleton<LocalFileHealthCheck>();
        services.AddSingleton<LocalFileStorageManager>();
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<LocalFileStorageManager>());
        services.AddHealthChecks().AddCheck<LocalFileHealthCheck>($"local_file:{Id}");
    }
}
