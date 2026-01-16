using System;
using System.Threading;
using System.Threading.Tasks;
using KitStack.Abstractions.Models;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Resolves the concrete <see cref="IFileStorageManager"/> for a given <see cref="StorageProvider"/>.
/// Also provides utilities for executing tasks against the resolved manager while managing concurrency.
/// </summary>
public interface IProviderManagerResolver
{
    /// <summary>
    /// Resolve the concrete IFileStorageManager instance for the given provider.
    /// </summary>
    Task<IFileStorageManager> ResolveManagerAsync(StorageProvider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a task against the resolved manager while managing a per-provider lock.
    /// </summary>
    Task<TResult> UseManagerAsync<TResult>(StorageProvider provider, Func<IFileStorageManager, Task<TResult>> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a task without returning a value against the resolved manager, while managing a per-provider lock.
    /// </summary>
    Task UseManagerAsync(StorageProvider provider, Func<IFileStorageManager, Task> action, CancellationToken cancellationToken = default);
}
