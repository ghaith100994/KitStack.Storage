using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using KitStack.Storage.Abstractions.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KitStack.Storage.Abstractions.Services;

/// <summary>Moves stored files between storage instances.</summary>
public interface IFileTransferService
{
    /// <summary>
    /// Copy <paramref name="entry"/> (and its image variants) to <paramref name="targetStorageName"/>: the plaintext is
    /// streamed from the source instance and re-encrypted with the target's keys, keeping the folder, file name and
    /// metadata; the copy is verified against the source checksum; the metadata is re-pointed with
    /// <see cref="IFileEntryStore.RelocateAsync(IReadOnlyCollection{FileRelocation}, CancellationToken)"/> (same <c>Id</c> and
    /// links); and the source is deleted only when <see cref="FileTransferOptions.DeleteSource"/> is set and everything
    /// else succeeded.
    /// </summary>
    /// <remarks>
    /// Idempotent and resumable: content the target already holds with the right checksum is not copied again, so a
    /// transfer that failed half-way can simply be run again. <paramref name="entry"/> is updated in place on success.
    /// <c>StoredFileRef</c> copies held by domain entities still point at the old location: resolve them by
    /// <c>FileEntryId</c> (<see cref="IFileEntryStore.FindManyAsync"/>).
    /// </remarks>
    Task<FileTransferResult> TransferAsync(IFileEntry entry, string targetStorageName, FileTransferOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Options of <see cref="IFileTransferService.TransferAsync"/>.</summary>
public sealed class FileTransferOptions
{
    /// <summary>Delete the source bytes once the copy is verified and the metadata re-pointed. Default <c>false</c>.</summary>
    public bool DeleteSource { get; set; }
}

/// <summary>Status of one transfer step.</summary>
public enum FileTransferStepStatus
{
    /// <summary>Done.</summary>
    Completed,

    /// <summary>Not needed (already done, or not requested).</summary>
    Skipped,

    /// <summary>Failed; later steps did not run.</summary>
    Failed,
}

/// <summary>One step of a transfer, for example <c>Copy original</c>, <c>Verify thumbnail</c>, <c>Relocate</c> or <c>DeleteSource</c>.</summary>
public sealed record FileTransferStep(string Name, FileTransferStepStatus Status, string? Detail = null);

/// <summary>Outcome of <see cref="IFileTransferService.TransferAsync"/>.</summary>
public sealed class FileTransferResult
{
    /// <summary>The entry (updated in place when the metadata was re-pointed).</summary>
    public required IFileEntry Entry { get; init; }

    /// <summary>Source instance.</summary>
    public required string SourceStorageName { get; init; }

    /// <summary>Target instance.</summary>
    public required string TargetStorageName { get; init; }

    /// <summary>Steps in execution order.</summary>
    public required IReadOnlyList<FileTransferStep> Steps { get; init; }

    /// <summary>True when no step failed.</summary>
    public bool Succeeded => Steps.All(s => s.Status != FileTransferStepStatus.Failed);
}

/// <summary>Default <see cref="IFileTransferService"/>.</summary>
internal sealed class FileTransferService(IFileStorageManagerProvider provider, IServiceProvider services, ILogger<FileTransferService>? logger = null)
    : IFileTransferService
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public async Task<FileTransferResult> TransferAsync(
        IFileEntry entry, string targetStorageName, FileTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new FileTransferOptions();
        var sourceName = StorageInstance.Normalize(entry.StorageName);
        var targetName = StorageInstance.Normalize(targetStorageName);
        var steps = new List<FileTransferStep>();
        FileTransferResult Result() => new() { Entry = entry, SourceStorageName = sourceName, TargetStorageName = targetName, Steps = steps };

        if (StorageInstance.AreSame(sourceName, targetName))
        {
            steps.Add(new FileTransferStep("Transfer", FileTransferStepStatus.Skipped, "The file is already stored in the target instance."));
            return Result();
        }

        var source = await provider.GetAsync(sourceName, cancellationToken).ConfigureAwait(false);
        var target = await provider.GetAsync(targetName, cancellationToken).ConfigureAwait(false);
        var sourceAccess = RequireAccess(source, sourceName);
        var targetAccess = RequireAccess(target, targetName);
        var store = services.GetService<IFileEntryStore>();

        var items = await CollectItemsAsync(entry, store, cancellationToken).ConfigureAwait(false);

        // Variants first, the original last: the original's metadata points at the variants' new locations.
        foreach (var item in items.OrderBy(i => i.IsOriginal))
        {
            var copied = await CopyAsync(item, source, targetAccess, steps, cancellationToken).ConfigureAwait(false);
            if (copied is null)
                return Result();
            item.Target = copied;
        }

        var original = items.Single(i => i.IsOriginal);
        var metadata = entry.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(entry.Metadata);
        RewriteVariantPaths(metadata, items);

        var relocations = items.Select(i => new FileRelocation(
                i.Id ?? Guid.Empty, targetName, i.Target!.Location, i.Target.StoragePath, i.Target.Encrypted, i.Target.StorageProvider,
                i.IsOriginal ? metadata : null))
            .Where(r => r.Id != Guid.Empty)
            .ToList();

        if (store is null)
        {
            steps.Add(new FileTransferStep("Relocate", FileTransferStepStatus.Skipped, "No IFileEntryStore is registered; only the entry object was updated."));
        }
        else
        {
            try
            {
                await store.RelocateAsync(relocations, cancellationToken).ConfigureAwait(false);
                steps.Add(new FileTransferStep("Relocate", FileTransferStepStatus.Completed, $"{relocations.Count} entries re-pointed."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Re-pointing file {FileEntryId} to {StorageName} failed", entry.Id, targetName);
                steps.Add(new FileTransferStep("Relocate", FileTransferStepStatus.Failed, ex.Message));
                return Result();
            }
        }

        entry.StorageName = targetName;
        entry.FileLocation = original.Target!.Location;
        entry.StoragePath = original.Target.StoragePath;
        entry.Encrypted = original.Target.Encrypted;
        entry.StorageProvider = original.Target.StorageProvider;
        entry.Metadata = metadata;

        if (!options.DeleteSource)
        {
            steps.Add(new FileTransferStep("DeleteSource", FileTransferStepStatus.Skipped, "Not requested."));
            return Result();
        }

        try
        {
            foreach (var item in items)
                await sourceAccess.DeleteContentAsync(item.Location, item.VariantType, cancellationToken).ConfigureAwait(false);
            steps.Add(new FileTransferStep("DeleteSource", FileTransferStepStatus.Completed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deleting the source of transferred file {FileEntryId} from {StorageName} failed", entry.Id, sourceName);
            steps.Add(new FileTransferStep("DeleteSource", FileTransferStepStatus.Failed, ex.Message));
        }

        return Result();
    }

    private async Task<StoredContent?> CopyAsync(
        TransferItem item, IFileStorageManager source, IStorageContentAccess target, List<FileTransferStep> steps, CancellationToken cancellationToken)
    {
        var label = item.IsOriginal ? "original" : item.VariantType;
        var fileName = Path.GetFileName(item.Location.Replace('\\', '/'));
        try
        {
            // Resume: the target already holds verified bytes from an earlier attempt.
            if (item.Checksum is not null && target.PredictLocation(item.Folder, fileName, item.VariantType) is { } predicted
                && await target.VerifyContentAsync(predicted, item.VariantType, cancellationToken).ConfigureAwait(false) is { } existing
                && existing.Checksum == item.Checksum)
            {
                steps.Add(new FileTransferStep($"Copy {label}", FileTransferStepStatus.Skipped, "The target already holds verified content."));
                return existing;
            }

            StoredContent written;
            var stream = await source.OpenReadAsync(item.SourceEntry, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                written = await target.PutContentAsync(new StorageContentWrite(item.Folder, fileName, item.ContentType, item.VariantType), stream, cancellationToken)
                    .ConfigureAwait(false);
            steps.Add(new FileTransferStep($"Copy {label}", FileTransferStepStatus.Completed));

            var expected = item.Checksum ?? written.Checksum;
            var verified = await target.VerifyContentAsync(written.Location, item.VariantType, cancellationToken).ConfigureAwait(false);
            if (written.Checksum != expected || verified?.Checksum != expected)
            {
                // Never leave unverified bytes behind; the source is untouched.
                await target.DeleteContentAsync(written.Location, item.VariantType, CancellationToken.None).ConfigureAwait(false);
                steps.Add(new FileTransferStep($"Verify {label}", FileTransferStepStatus.Failed,
                    $"Checksum mismatch: expected {expected}, source read {written.Checksum}, target holds {verified?.Checksum ?? "nothing"}."));
                return null;
            }

            steps.Add(new FileTransferStep($"Verify {label}", FileTransferStepStatus.Completed));
            return verified;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Copying {Variant} of file {FileEntryId} failed", label, item.Id);
            steps.Add(new FileTransferStep($"Copy {label}", FileTransferStepStatus.Failed, ex.Message));
            return null;
        }
    }

    private static async Task<List<TransferItem>> CollectItemsAsync(IFileEntry entry, IFileEntryStore? store, CancellationToken cancellationToken)
    {
        var folder = FolderOf(entry);
        var items = new List<TransferItem>
        {
            new(entry.Id, entry.FileLocation, string.IsNullOrWhiteSpace(entry.VariantType) ? FileStorageManagerBase.OriginalVariant : entry.VariantType,
                folder, entry.Checksum, entry.ContentType, entry, IsOriginal: true, MetadataKey: null),
        };

        if (entry.Metadata is not { Count: > 0 } metadata)
            return items;

        IReadOnlyList<IFileEntry> records = [];
        if (store is not null)
        {
            try
            {
                records = (await store.QueryAsync(new FileEntryQuery { OriginalId = entry.Id, Take = 1000 }, cancellationToken).ConfigureAwait(false)).Items;
            }
            catch (NotSupportedException)
            {
                // Variant ids are unknown: their bytes still move, only the original's record is re-pointed.
            }
        }

        foreach (var (location, variantType, key) in VariantLocations(metadata))
        {
            var record = records.FirstOrDefault(r => r.FileLocation == location);
            var variantFolder = record?.Metadata is { } rm && rm.TryGetValue(FileEntryMetadataKeys.Folder, out var f) && !string.IsNullOrWhiteSpace(f)
                ? f
                : StoragePaths.Combine(folder, Path.GetFileName(Path.GetDirectoryName(location.Replace('\\', '/')) ?? string.Empty));
            var sourceEntry = new FileEntry
            {
                Id = record?.Id ?? Guid.NewGuid(),
                FileLocation = location,
                VariantType = variantType,
                StorageName = entry.StorageName,
                Encrypted = record?.Encrypted ?? entry.Encrypted,
                Size = record?.Size ?? 0,
            };
            items.Add(new TransferItem(record?.Id, location, variantType, variantFolder, record?.Checksum,
                record?.ContentType ?? ContentTypes.Jpeg, sourceEntry, IsOriginal: false, key));
        }

        return items;
    }

    /// <summary>The logical folder of an entry: recorded since 0.3, otherwise the directory of its location.</summary>
    private static string FolderOf(IFileEntry entry)
    {
        if (entry.Metadata is { } metadata && metadata.TryGetValue(FileEntryMetadataKeys.Folder, out var folder) && !string.IsNullOrWhiteSpace(folder))
            return folder;
        var directory = Path.GetDirectoryName(entry.FileLocation.Replace('\\', '/'))?.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(directory) ? "files" : directory;
    }

    private static IEnumerable<(string Location, string VariantType, string Key)> VariantLocations(IDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue(FileEntryMetadataKeys.CompressedPath, out var compressed) && !string.IsNullOrWhiteSpace(compressed))
            yield return (compressed, "compressed", FileEntryMetadataKeys.CompressedPath);
        if (metadata.TryGetValue(FileEntryMetadataKeys.ThumbnailPath, out var thumbnail) && !string.IsNullOrWhiteSpace(thumbnail))
            yield return (thumbnail, "thumbnail", FileEntryMetadataKeys.ThumbnailPath);
        if (metadata.TryGetValue(FileEntryMetadataKeys.Variants, out var sizes) && !string.IsNullOrWhiteSpace(sizes))
        {
            foreach (var path in sizes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var sizeName = Path.GetFileName(Path.GetDirectoryName(path.Replace('\\', '/')) ?? string.Empty);
                yield return (path, string.IsNullOrEmpty(sizeName) ? FileStorageManagerBase.OriginalVariant : sizeName, FileEntryMetadataKeys.Variants);
            }
        }
    }

    private static void RewriteVariantPaths(Dictionary<string, string> metadata, List<TransferItem> items)
    {
        var sizes = new List<string>();
        foreach (var item in items.Where(i => !i.IsOriginal))
        {
            if (item.MetadataKey == FileEntryMetadataKeys.Variants)
                sizes.Add(item.Target!.Location);
            else
                metadata[item.MetadataKey!] = item.Target!.Location;
        }

        if (sizes.Count > 0)
            metadata[FileEntryMetadataKeys.Variants] = string.Join(';', sizes);
    }

    private static IStorageContentAccess RequireAccess(IFileStorageManager manager, string name)
        => manager as IStorageContentAccess
           ?? throw new NotSupportedException($"Storage instance '{name}' ({manager.GetType().Name}) does not support content transfers.");

    private sealed record TransferItem(
        DefaultIdType? Id, string Location, string VariantType, string Folder, string? Checksum, string ContentType,
        IFileEntry SourceEntry, bool IsOriginal, string? MetadataKey)
    {
        public StoredContent? Target { get; set; }
    }
}
