using System;
using System.Collections.Generic;
using KitStack.Abstractions.Models;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Registry for provider definitions (register, find, update options).
/// </summary>
public interface IStorageProviderRegistry
{
    /// <summary>
    /// Get all registered storage providers.
    /// </summary>
    IReadOnlyCollection<StorageProvider> GetAll();

    /// <summary>
    /// Get a storage provider by ID.
    /// </summary>
    StorageProvider? GetById(string id);

    /// <summary>
    /// Get a storage provider by GUID ID.
    /// </summary>
    StorageProvider? GetById(Guid id);

    /// <summary>
    /// Get the default storage provider (as marked).
    /// </summary>
    StorageProvider? GetDefault();

    /// <summary>
    /// Register a provider.
    /// </summary>
    void Register(StorageProvider provider);

    /// <summary>
    /// Update the options for an existing provider.
    /// </summary>
    bool TryUpdateOptions(string id, object options);

    /// <summary>
    /// Retrieve typed options for an existing provider.
    /// </summary>
    bool TryGetOptions<TOptions>(string id, out TOptions? options) where TOptions : class;
}
