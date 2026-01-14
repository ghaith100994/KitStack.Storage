using System;
using KitStack.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Abstractions.Models;

/// <summary>
/// Base descriptor for a storage provider. Providers encapsulate their registration logic and expose a unique identifier
/// so consumers can resolve and trace which provider is active within the application.
/// </summary>
public abstract class StorageProvider
{
    /// <summary>
    /// Create a provider with the supplied unique identifier.
    /// </summary>
    /// <param name="id">Unique identifier for the provider instance.</param>
    protected StorageProvider(string id) => Id = id;

    /// <summary>
    /// Unique identifier for this provider instance.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Friendly name used for diagnostics/logging.
    /// </summary>
    public virtual string Name => GetType().Name;

    /// <summary>
    /// Allows the provider to register its required services, including the concrete <see cref="IFileStorageManager"/> implementation.
    /// </summary>
    public abstract void ConfigureServices(IServiceCollection services);

    public override string ToString() => $"{Name} ({Id})";
}
