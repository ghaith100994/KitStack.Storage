using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;

namespace KitStack.Abstractions.Services;

public class StorageProviderRegistry : IStorageProviderRegistry
{
    private readonly ConcurrentDictionary<Guid, StorageProvider> _providers = new();

    public IReadOnlyCollection<StorageProvider> GetAll() => [.. _providers.Values];

    public StorageProvider? GetById(string id)
    {
        if (Guid.TryParse(id, out var gid))
            return _providers.TryGetValue(gid, out var provider) ? provider : null;

        return null;
    }

    public StorageProvider? GetById(Guid id) => _providers.TryGetValue(id, out var provider) ? provider : null;

    public StorageProvider? GetDefault() => _providers.Values.FirstOrDefault(p => p.IsDefault);

    public void Register(StorageProvider provider)
    {
        _providers[provider.Id] = provider;
    }

    public bool TryUpdateOptions(string id, object options)
    {
        var provider = GetById(id);
        if (provider != null)
        {
            provider.Options = options;
            return true;
        }
        return false;
    }

    public bool TryGetOptions<TOptions>(string id, out TOptions? options) where TOptions : class
    {
        var provider = GetById(id);
        if (provider?.Options is TOptions typedOptions)
        {
            options = typedOptions;
            return true;
        }

        options = null;
        return false;
    }
}
