using KitStack.Abstractions.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KitStack.Abstractions.Services;

/// <summary>
/// Deletes files that an <see cref="IFileEntryStore"/> reports as orphans: uploaded longer ago than a grace
/// period and no longer linked to any entity (for example the old file left behind by an update).
/// </summary>
public sealed class FileOrphanSweeper(IFileEntryStore store, IFileStorageManager storage, ILogger<FileOrphanSweeper>? logger = null)
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>
    /// Delete up to <paramref name="batchSize"/> orphans older than <paramref name="minAge"/>.
    /// A failure on one file is logged and does not stop the others.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    public async Task<int> SweepAsync(TimeSpan minAge, int batchSize = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var orphans = await store.ListOrphansAsync(DateTimeOffset.UtcNow - minAge, batchSize, cancellationToken).ConfigureAwait(false);
        var deleted = 0;

        foreach (var orphan in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await storage.DeleteAsync(orphan, cancellationToken).ConfigureAwait(false);
                await store.MarkDeletedAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not delete orphaned file {FileEntryId} at {FileLocation}", orphan.Id, orphan.FileLocation);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Deleted {Count} orphaned files", deleted);
        return deleted;
    }
}
