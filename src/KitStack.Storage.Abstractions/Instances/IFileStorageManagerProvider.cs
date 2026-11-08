using System.Diagnostics.CodeAnalysis;
using KitStack.Storage.Abstractions.Exceptions;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;

namespace KitStack.Storage.Abstractions.Instances;

/// <summary>
/// Resolves the <see cref="IFileStorageManager"/> of a storage instance: statically registered ones
/// (<c>AddS3("name")</c>, the unnamed <c>default</c>) and runtime ones from an <see cref="IStorageInstanceSource"/>,
/// which are built on first use, cached, rebuilt when their version changes and disposed safely. Singleton, thread-safe.
/// </summary>
/// <remarks>
/// Managers returned for runtime instances stay valid after the instance was rebuilt or invalidated: calls are
/// forwarded to the current build, and the replaced build is disposed once its in-flight operations and open read
/// streams have completed. When file entry tracking is enabled, returned managers record created files in the store.
/// </remarks>
public interface IFileStorageManagerProvider
{
    /// <summary>Names of the static instances and of the runtime instances currently cached.</summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>The manager of the instance <paramref name="name"/> (case-insensitive).</summary>
    /// <exception cref="StorageInstanceNotFoundException">No such instance.</exception>
    /// <exception cref="StorageInstanceValidationException">The runtime definition is invalid.</exception>
    /// <remarks>Prefer <see cref="GetAsync"/> where possible: the first resolution of a runtime instance queries the source.</remarks>
    IFileStorageManager Get(string name);

    /// <summary>Asynchronous <see cref="Get(string)"/>.</summary>
    ValueTask<IFileStorageManager> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="Get(string)"/>, but returns <c>false</c> when the instance does not exist.</summary>
    bool TryGet(string name, [NotNullWhen(true)] out IFileStorageManager? manager);

    /// <summary>The manager that owns <paramref name="entry"/> (<see cref="IFileEntry.StorageName"/>, <c>default</c> when null).</summary>
    IFileStorageManager Get(IFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Get(StorageInstance.Normalize(entry.StorageName));
    }

    /// <summary>The manager that owns <paramref name="reference"/> (<c>default</c> when its <c>StorageName</c> is null).</summary>
    IFileStorageManager Get(StoredFileRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Get(StorageInstance.Normalize(reference.StorageName));
    }

    /// <summary>Capabilities of the instance <paramref name="name"/>.</summary>
    StorageCapabilities GetCapabilities(string name);

    /// <summary>Drop the cached build of a runtime instance; the next resolution asks the source again. No-op for static instances.</summary>
    void Invalidate(string name);

    /// <summary>Drop every cached runtime instance.</summary>
    void InvalidateAll();
}
