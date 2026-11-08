using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Abstractions.Services;

/// <summary>Options of <see cref="IFileVersionService.ReplaceAsync"/>.</summary>
public sealed class ReplaceOptions
{
    /// <summary>
    /// Keep the replaced entry as a previous version instead of deleting it: it is marked superseded, its active links
    /// move to the new entry, and the new entry records <see cref="IFileEntry.PreviousVersionId"/>. Default <c>false</c>.
    /// </summary>
    public bool KeepPreviousVersion { get; set; }

    /// <summary>Instance that stores the new version. Default: the instance of the replaced entry.</summary>
    public string? StorageName { get; set; }
}

/// <summary>File versions on top of an <see cref="IFileEntryStore"/>. History is never mutated: restoring creates a new version.</summary>
public interface IFileVersionService
{
    /// <summary>Store <paramref name="content"/> as the replacement of <paramref name="existing"/>; see <see cref="ReplaceOptions"/>.</summary>
    Task<IFileEntry> ReplaceAsync(IFileEntry existing, StorageWriteOptions options, Stream content, ReplaceOptions? replaceOptions = null, CancellationToken cancellationToken = default);

    /// <summary>Every non-deleted version in the chain of <paramref name="id"/>, newest first.</summary>
    Task<IReadOnlyList<IFileEntry>> ListVersionsAsync(DefaultIdType id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Make the content of <paramref name="versionId"/> current again by creating a new version from its bytes (the
    /// current version is kept as a previous version). Returns the new current entry, or the entry itself when it is current.
    /// </summary>
    Task<IFileEntry> RestoreVersionAsync(DefaultIdType versionId, CancellationToken cancellationToken = default);

    /// <summary>Delete all but the <paramref name="keepLatest"/> most recent previous versions of the chain of <paramref name="id"/> (the current version is always kept). Returns the number deleted.</summary>
    Task<int> PurgeVersionsAsync(DefaultIdType id, int keepLatest, CancellationToken cancellationToken = default);
}

internal sealed class FileVersionService(IFileStorageManagerProvider provider, IServiceProvider services) : IFileVersionService
{
    private static readonly string[] SystemKeys =
    [
        FileEntryMetadataKeys.OriginalId, FileEntryMetadataKeys.CompressedPath, FileEntryMetadataKeys.ThumbnailPath,
        FileEntryMetadataKeys.Variants, FileEntryMetadataKeys.Folder,
    ];

    private IFileEntryStore Store => services.GetService<IFileEntryStore>()
        ?? throw new StorageConfigurationException("File versions need an IFileEntryStore (AddEntityFrameworkCoreStore, AddMongoStore or AddInMemoryStore).");

    public async Task<IFileEntry> ReplaceAsync(
        IFileEntry existing, StorageWriteOptions options, Stream content, ReplaceOptions? replaceOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);
        replaceOptions ??= new ReplaceOptions();
        var storage = await provider.GetAsync(StorageInstance.Normalize(replaceOptions.StorageName ?? existing.StorageName), cancellationToken).ConfigureAwait(false);

        if (!replaceOptions.KeepPreviousVersion)
        {
            var replacement = await storage.CreateAsync(options, content, cancellationToken).ConfigureAwait(false);
            await provider.Get(existing).DeleteAsync(existing, cancellationToken).ConfigureAwait(false);
            return replacement;
        }

        var store = Store;
        var created = await storage.CreateAsync(options, content, cancellationToken).ConfigureAwait(false);
        if (await store.FindAsync(created.Id, cancellationToken).ConfigureAwait(false) is null)
            await store.AddAsync(created, cancellationToken).ConfigureAwait(false);
        await store.SupersedeAsync(existing.Id, created.Id, cancellationToken).ConfigureAwait(false);

        // Mirror the stored state on the objects the caller holds.
        created.PreviousVersionId = existing.Id;
        created.VersionNumber = existing.VersionNumber + 1;
        existing.SupersededById = created.Id;
        if (existing.RelatedEntities is { Count: > 0 } links)
        {
            created.RelatedEntities ??= [];
            foreach (var link in links.Where(l => l.IsUsed).ToList())
            {
                if (!created.RelatedEntities.Any(r => r.EntityName == link.EntityName && r.EntityId == link.EntityId))
                    created.RelatedEntities.Add(new FileRelatedEntity { EntityName = link.EntityName, EntityId = link.EntityId, Notes = link.Notes, IsUsed = true, LinkedOn = link.LinkedOn });
                links.Remove(link);
            }
        }

        return created;
    }

    public Task<IReadOnlyList<IFileEntry>> ListVersionsAsync(DefaultIdType id, CancellationToken cancellationToken = default)
        => Store.ListVersionsAsync(id, cancellationToken);

    public async Task<IFileEntry> RestoreVersionAsync(DefaultIdType versionId, CancellationToken cancellationToken = default)
    {
        var store = Store;
        var version = await store.FindAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (version is null || version.IsDeleted)
            throw new StorageNotFoundException($"File version '{versionId}' was not found.");

        var versions = await store.ListVersionsAsync(versionId, cancellationToken).ConfigureAwait(false);
        var current = versions.Count > 0 ? versions[0] : version;
        if (current.Id == version.Id)
            return current;

        var metadata = version.Metadata is null
            ? new Dictionary<string, string>()
            : version.Metadata.Where(kv => !SystemKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var options = new StorageWriteOptions
        {
            FileName = version.FileName,
            Category = version.Category,
            Folder = FolderOf(current) ?? FolderOf(version) ?? "restored",
            Metadata = metadata,
        };

        var content = await provider.Get(version).OpenReadAsync(version, cancellationToken).ConfigureAwait(false);
        await using (content.ConfigureAwait(false))
            return await ReplaceAsync(current, options, content, new ReplaceOptions { KeepPreviousVersion = true, StorageName = current.StorageName }, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<int> PurgeVersionsAsync(DefaultIdType id, int keepLatest, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepLatest);
        var store = Store;
        var versions = await store.ListVersionsAsync(id, cancellationToken).ConfigureAwait(false);
        var purge = versions.Skip(1).Skip(keepLatest).ToList();
        foreach (var version in purge)
        {
            await provider.Get(version).DeleteAsync(version, cancellationToken).ConfigureAwait(false);
            await store.MarkDeletedAsync(version.Id, cancellationToken).ConfigureAwait(false);
        }

        return purge.Count;
    }

    private static string? FolderOf(IFileEntry entry)
    {
        if (entry.Metadata is { } metadata && metadata.TryGetValue(FileEntryMetadataKeys.Folder, out var folder) && !string.IsNullOrWhiteSpace(folder))
            return folder;
        var directory = Path.GetDirectoryName(entry.FileLocation.Replace('\\', '/'))?.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }
}
