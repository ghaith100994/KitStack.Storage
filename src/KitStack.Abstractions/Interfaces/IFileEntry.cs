using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using KitStack.Abstractions.Models;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Represents metadata for a file stored by a provider.
/// Implementers (providers or application models) should follow this contract.
/// </summary>
public interface IFileEntry
{
    /// <summary>
    /// Identifier for the file entry. The concrete type is provider/application-specific.
    /// </summary>
    DefaultIdType Id { get; set; }

    /// <summary>
    /// Original file name including extension (for example "photo.jpg").
    /// </summary>
    string FileName { get; set; }

    /// <summary>
    /// Logical or provider-specific location/key for the stored file (for example "app/images/2026/photo.jpg" or an S3 key).
    /// This value is intended to be persisted by callers and used to retrieve the file from the provider.
    /// </summary>
    string FileLocation { get; set; }

    /// <summary>
    /// Optional logical category or module name used to group files (for example "Users" or "Products").
    /// Providers may use this value to organize storage layout.
    /// </summary>
    string? Category { get; set; }

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    long Size { get; set; }

    /// <summary>
    /// Optional MIME/content type (for example "image/jpeg").
    /// </summary>
    string? ContentType { get; set; }

    /// <summary>
    /// Optional file extension including the leading dot (for example ".jpg").
    /// </summary>
    string? FileExtension { get; set; }

    /// <summary>
    /// Provider-agnostic key/value metadata attached to this file.
    /// Providers may store additional information such as variant paths here.
    /// </summary>
    IDictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// When the file was uploaded (UTC).
    /// </summary>
    DateTimeOffset UploadedTime { get; set; }

    /// <summary>
    /// Optional variant classification for this entry (for example "original", "thumbnail", "compressed", "small").
    /// Providers should set this for variant files to make querying and display easier.
    /// </summary>
    string? VariantType { get; set; }

    /// <summary>
    /// Indicates whether the file content is stored encrypted by the provider.
    /// </summary>
    bool Encrypted { get; set; }

    /// <summary>
    /// Original file name provided by the uploader before sanitization.
    /// </summary>
    string? OriginalFileName { get; set; }

    /// <summary>
    /// Logical storage provider identifier (Local, S3, etc.).
    /// </summary>
    string? StorageProvider { get; set; }

    /// <summary>
    /// When the file was last accessed/read.
    /// </summary>
    DateTimeOffset? LastAccessedTime { get; set; }

    /// <summary>
    /// Indicates whether the file has been soft-deleted.
    /// </summary>
    bool IsDeleted { get; set; }

    /// <summary>
    /// Optional relationships linking this file to one or more domain entities.
    /// </summary>
    ICollection<FileRelatedEntity>? RelatedEntities { get; set; }
}


/// <summary>
/// Resolves the concrete <see cref="IFileStorageManager"/> for a given <see cref="StorageProvider"/>
/// and provides a mechanism to perform operations against that manager while holding a per-provider lock.
/// Use <see cref="UseManagerAsync{TResult}"/> to run an action against the resolved manager safely.
/// </summary>
public interface IProviderManagerResolver
{
    /// <summary>
    /// Resolve the concrete IFileStorageManager instance for the given provider.
    /// This may use <see cref="StorageProvider.ManagerType"/>, or fall back to discovery by registered managers
    /// (matching by provider.ProviderType or provider.Name).
    /// </summary>
    /// <param name="provider">The provider descriptor to resolve the manager for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved manager instance.</returns>
    Task<IFileStorageManager> ResolveManagerAsync(StorageProvider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute the provided async action against the manager resolved for the given provider while holding
    /// a per-provider lock (serializes concurrent callers for the same provider).
    /// The lock is released after the action completes (or throws). Useful for operations that must not run concurrently
    /// for the same provider (for example, reconfiguration, bucket creation, or atomic multi-step uploads).
    /// </summary>
    /// <typeparam name="TResult">Action result type.</typeparam>
    /// <param name="provider">Provider descriptor (must not be null).</param>
    /// <param name="action">Action to run using the resolved manager.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Action result.</returns>
    Task<TResult> UseManagerAsync<TResult>(StorageProvider provider, Func<IFileStorageManager, Task<TResult>> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="UseManagerAsync{TResult}"/> but for actions that don't return a value.
    /// </summary>
    Task UseManagerAsync(StorageProvider provider, Func<IFileStorageManager, Task> action, CancellationToken cancellationToken = default);
}


/// <summary>
/// Default implementation of <see cref="IProviderManagerResolver"/>.
/// - Resolves manager via provider.ManagerType when present (Type.GetType + DI)
/// - Otherwise enumerates registered IFileStorageManager services and matches by type name using provider.ProviderType or provider.Name
/// - Falls back to single registered manager if only one exists
/// - Provides a per-provider SemaphoreSlim lock to serialize concurrent operations for the same provider
/// </summary>
public class ProviderManagerResolver : IProviderManagerResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public ProviderManagerResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async Task<IFileStorageManager> ResolveManagerAsync(StorageProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // 1) Preferred: use ManagerType if set
        if (!string.IsNullOrWhiteSpace(provider.ManagerType))
        {
            var mgrType = Type.GetType(provider.ManagerType, throwOnError: false, ignoreCase: true);
            if (mgrType != null)
            {
                // Try resolve by concrete type from DI
                var mgr = _serviceProvider.GetService(mgrType) as IFileStorageManager;
                if (mgr != null) return mgr;

                // Try resolve by asking for IFileStorageManager (single registration) and checking type
                var possible = _serviceProvider.GetService(typeof(IFileStorageManager));
                if (possible is IFileStorageManager singleMgr && mgrType.IsInstanceOfType(singleMgr))
                    return singleMgr;
            }
        }

        // 2) Discovery: enumerate all IFileStorageManager instances and match by provider info
        var managers = _serviceProvider.GetServices<IFileStorageManager>()?.ToList() ?? new List<IFileStorageManager>();

        if (managers.Count == 0)
            throw new InvalidOperationException("No IFileStorageManager implementations are registered in DI.");

        // Normalize matching candidates: include ProviderType and Name
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(provider.ProviderType))
            candidates.Add(provider.ProviderType.Trim());
        if (!string.IsNullOrWhiteSpace(provider.Name))
            candidates.Add(provider.Name.Trim());

        // Try to find a manager whose concrete type name matches patterns like "S3FileStorageManager", "SftpFileStorageManager",
        // or contains the provider type/name fragment.
        foreach (var mgr in managers)
        {
            var tname = mgr.GetType().Name; // concrete type name
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (tname.EndsWith($"{candidate}FileStorageManager", StringComparison.OrdinalIgnoreCase) ||
                    tname.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return mgr;
                }
            }
        }

        // 3) If only a single manager is registered, use it
        if (managers.Count == 1)
            return managers[0];

        // 4) Not found - provide a clear error mentioning registered manager types and requested provider
        var registered = string.Join(", ", managers.Select(m => m.GetType().FullName));
        throw new InvalidOperationException($"Unable to locate a suitable IFileStorageManager for provider '{provider.Name}' (ProviderType='{provider.ProviderType}'). Registered managers: {registered}");
    }

    public async Task<TResult> UseManagerAsync<TResult>(StorageProvider provider, Func<IFileStorageManager, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (action == null) throw new ArgumentNullException(nameof(action));

        var key = provider.Id == Guid.Empty ? Guid.Empty : provider.Id;
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mgr = await ResolveManagerAsync(provider, cancellationToken).ConfigureAwait(false);
            return await action(mgr).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task UseManagerAsync(StorageProvider provider, Func<IFileStorageManager, Task> action, CancellationToken cancellationToken = default)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (action == null) throw new ArgumentNullException(nameof(action));

        var key = provider.Id == Guid.Empty ? Guid.Empty : provider.Id;
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mgr = await ResolveManagerAsync(provider, cancellationToken).ConfigureAwait(false);
            await action(mgr).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }
}
