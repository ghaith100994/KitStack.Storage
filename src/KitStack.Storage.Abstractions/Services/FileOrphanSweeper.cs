using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KitStack.Storage.Abstractions.Services;

/// <summary>
/// Deletes files that an <see cref="IFileEntryStore"/> reports as orphans: uploaded longer ago than a grace
/// period and no longer linked to any entity (for example the old file left behind by an update). Previous versions
/// kept by <c>ReplaceOptions.KeepPreviousVersion</c> are never orphans.
/// </summary>
/// <remarks>
/// With an <see cref="IFileStorageManagerProvider"/> (the default when registered through DI) each orphan is deleted by
/// the instance that owns it; a failing instance is logged and skipped without stopping the others.
/// Without one, every orphan is deleted through <c>storage</c> (0.2 behaviour).
/// </remarks>
public sealed class FileOrphanSweeper
{
    private readonly IFileEntryStore _store;
    private readonly IFileStorageManager? _storage;
    private readonly IFileStorageManagerProvider? _provider;
    private readonly ILogger _logger;

    /// <summary>Create the sweeper.</summary>
    /// <param name="store">Metadata store.</param>
    /// <param name="storage">Single manager used when no <paramref name="provider"/> is given.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="provider">Routes each orphan to the instance that owns it.</param>
    public FileOrphanSweeper(IFileEntryStore store, IFileStorageManager? storage = null, ILogger<FileOrphanSweeper>? logger = null, IFileStorageManagerProvider? provider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (storage is null && provider is null)
            throw new ArgumentException("A storage manager or a storage manager provider is required.", nameof(storage));
        _storage = storage;
        _provider = provider;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// Delete up to <paramref name="batchSize"/> orphans older than <paramref name="minAge"/>.
    /// A failure on one file is logged and does not stop the others.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    public Task<int> SweepAsync(TimeSpan minAge, int batchSize = 100, CancellationToken cancellationToken = default)
        => SweepAsync(minAge, batchSize, storageName: null, cancellationToken);

    /// <summary>Like <see cref="SweepAsync(TimeSpan, int, CancellationToken)"/>, limited to one instance when <paramref name="storageName"/> is set.</summary>
    public async Task<int> SweepAsync(TimeSpan minAge, int batchSize, string? storageName, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var before = DateTimeOffset.UtcNow - minAge;
        // The original overload (which excludes kept previous versions too) unless an instance filter is requested.
        var orphans = storageName is null
            ? await _store.ListOrphansAsync(before, batchSize, cancellationToken).ConfigureAwait(false)
            : await _store.ListOrphansAsync(before, batchSize, storageName, includeSupersededVersions: false, cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        var failedInstances = new HashSet<string>(StorageInstance.Comparer);

        foreach (var orphan in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = StorageInstance.Normalize(orphan.StorageName);
            if (failedInstances.Contains(instance))
                continue;

            IFileStorageManager storage;
            try
            {
                storage = _provider is null ? _storage! : await _provider.GetAsync(instance, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedInstances.Add(instance);
                _logger.LogWarning(ex, "Skipping orphaned files of storage instance {StorageName}: the instance is unavailable", instance);
                continue;
            }

            try
            {
                await storage.DeleteAsync(orphan, cancellationToken).ConfigureAwait(false);
                await _store.MarkDeletedAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not delete orphaned file {FileEntryId} at {FileLocation} in {StorageName}", orphan.Id, orphan.FileLocation, instance);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Deleted {Count} orphaned files", deleted);
        return deleted;
    }
}
