using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace KitStack.AspNetCore.Services;

// Provider resolver lives in the host-related project (AspNetCore) not in Abstractions
public class ProviderManagerResolver : IProviderManagerResolver
{
    private readonly IServiceProvider _sp;

    public ProviderManagerResolver(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<IFileStorageManager> ResolveManagerAsync(StorageProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // 1) If the provider explicitly specifies a manager CLR type, try to resolve it first
        if (!string.IsNullOrWhiteSpace(provider.ManagerType))
        {
            var mgrType = Type.GetType(provider.ManagerType);
            if (mgrType != null)
            {
                var mgr = _sp.GetService(mgrType) as IFileStorageManager;
                if (mgr != null)
                {
                ApplyOptionsIfConfigurable(mgr, provider);
                    return mgr;
                }
            }
        }

        // 2) try to resolve by provider name or type by looking up registrations (StorageProviderRegistration entries contributed to DI)
        var registrations = _sp.GetServices<StorageProviderRegistration>()?.ToList() ?? new List<StorageProviderRegistration>();

        StorageProviderRegistration? registration = null;

        if (provider.Id != default)
            registration = registrations.FirstOrDefault(r => r.Provider.Id == provider.Id);

        if (registration == null && !string.IsNullOrWhiteSpace(provider.Name))
            registration = registrations.FirstOrDefault(r => string.Equals(r.Provider.Name, provider.Name, StringComparison.OrdinalIgnoreCase));

        if (registration == null && !string.IsNullOrWhiteSpace(provider.ProviderType))
            registration = registrations.FirstOrDefault(r => string.Equals(r.Provider.ProviderType, provider.ProviderType, StringComparison.OrdinalIgnoreCase)
                                                              || string.Equals(r.Provider.Name, provider.ProviderType, StringComparison.OrdinalIgnoreCase));

        if (registration != null && registration.ManagerType != null)
        {
            var mgr = _sp.GetService(registration.ManagerType) as IFileStorageManager;
            if (mgr != null)
            {
                ApplyOptionsIfConfigurable(mgr, provider ?? registration.Provider);
                return mgr;
            }
        }

        // last resort: resolve IFileStorageManager
        var defaultMgr = _sp.GetService(typeof(IFileStorageManager)) as IFileStorageManager;
        if (defaultMgr != null)
        {
        ApplyOptionsIfConfigurable(defaultMgr, provider);
            return defaultMgr;
        }

        throw new InvalidOperationException($"No manager found for provider '{provider.Name ?? provider.Id.ToString()}'");
    }

    public Task<TResult> UseManagerAsync<TResult>(StorageProvider provider, Func<IFileStorageManager, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        return ResolveManagerAsync(provider, cancellationToken).ContinueWith(async t => await action(t.Result), cancellationToken).Unwrap();
    }

    public Task UseManagerAsync(StorageProvider provider, Func<IFileStorageManager, Task> action, CancellationToken cancellationToken = default)
    {
        return ResolveManagerAsync(provider, cancellationToken).ContinueWith(async t => await action(t.Result), cancellationToken).Unwrap();
    }

    private void ApplyOptionsIfConfigurable(IFileStorageManager mgr, StorageProvider? provider)
    {
        if (mgr == null) return;
        if (provider == null) return;

        if (mgr is IConfigurableProvider cfg)
        {
            try
            {
                cfg.UpdateOptions(provider);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
