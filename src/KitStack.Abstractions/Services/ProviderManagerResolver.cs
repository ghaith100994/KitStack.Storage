using System;
using System.Threading;
using System.Threading.Tasks;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;

namespace KitStack.Abstractions.Services;

public class ProviderManagerResolver : IProviderManagerResolver
{
    private readonly IServiceProvider _sp;

    public ProviderManagerResolver(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<IFileStorageManager> ResolveManagerAsync(StorageProvider provider, CancellationToken cancellationToken = default)
    {
        var mgrType = Type.GetType(provider.ManagerType);
        var mgr = _sp.GetService(mgrType ?? typeof(IFileStorageManager)) as IFileStorageManager;

        if (mgr == null)
            throw new InvalidOperationException($"No manager found for provider '{provider.Name}'");

        return mgr;
    }

    public Task<TResult> UseManagerAsync<TResult>(StorageProvider provider, Func<IFileStorageManager, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        return ResolveManagerAsync(provider, cancellationToken).ContinueWith(async t => await action(t.Result), cancellationToken).Unwrap();
    }

    public Task UseManagerAsync(StorageProvider provider, Func<IFileStorageManager, Task> action, CancellationToken cancellationToken = default)
    {
        return ResolveManagerAsync(provider, cancellationToken).ContinueWith(async t => await action(t.Result), cancellationToken).Unwrap();
    }
}
