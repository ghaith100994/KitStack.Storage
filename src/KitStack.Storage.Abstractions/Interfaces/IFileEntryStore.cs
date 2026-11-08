using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Models;

namespace KitStack.Storage.Abstractions.Interfaces;

/// <summary>
/// Persistent store for <see cref="IFileEntry"/> metadata and the links between files and domain entities.
/// Storage providers keep the bytes; an <see cref="IFileEntryStore"/> keeps track of what was stored, who uses it,
/// and which files have become orphans. KitStack.Storage.EntityFrameworkCore ships an EF Core implementation.
/// </summary>
public interface IFileEntryStore
{
    /// <summary>Insert a new entry (including its <see cref="IFileEntry.RelatedEntities"/>). Inserting an existing id is a no-op.</summary>
    Task AddAsync(IFileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the stored content fields of an existing entry (<c>Size</c>, <c>Checksum</c>, <c>ContentType</c>,
    /// <c>Encrypted</c>, <c>LastAccessedTime</c>, <c>Metadata</c>) after its bytes were overwritten. Unknown ids are ignored.
    /// </summary>
    Task UpdateAsync(IFileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Find an entry by id, or <c>null</c>.</summary>
    Task<IFileEntry?> FindAsync(DefaultIdType id, CancellationToken cancellationToken = default);

    /// <summary>Find a non-deleted entry by its provider-relative <see cref="IFileEntry.FileLocation"/>, or <c>null</c>.</summary>
    Task<IFileEntry?> FindByLocationAsync(string fileLocation, CancellationToken cancellationToken = default);

    /// <summary>List non-deleted entries linked to an entity.</summary>
    /// <param name="entityName">Logical entity name, for example the CLR type name.</param>
    /// <param name="entityId">Entity identifier in invariant string form.</param>
    /// <param name="includeUnused">Also return links that were released (<c>IsUsed = false</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<IFileEntry>> ListByEntityAsync(string entityName, string entityId, bool includeUnused = false, CancellationToken cancellationToken = default);

    /// <summary>Record (or re-activate) a link between a file and an entity.</summary>
    Task LinkAsync(DefaultIdType fileEntryId, string entityName, string entityId, string? notes = null, CancellationToken cancellationToken = default);

    /// <summary>Release a link between a file and an entity (<c>IsUsed = false</c>). Unknown links are ignored.</summary>
    Task UnlinkAsync(DefaultIdType fileEntryId, string entityName, string entityId, CancellationToken cancellationToken = default);

    /// <summary>Mark an entry as deleted. Unknown ids are ignored.</summary>
    Task MarkDeletedAsync(DefaultIdType id, CancellationToken cancellationToken = default);

    /// <summary>
    /// List non-deleted entries uploaded before <paramref name="uploadedBefore"/> that have no active link.
    /// Image variants are not returned on their own: they are removed together with their original. Previous versions
    /// kept by <c>ReplaceOptions.KeepPreviousVersion</c> (<see cref="IFileEntry.SupersededById"/> set) are not orphans.
    /// </summary>
    Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(DateTimeOffset uploadedBefore, int maxCount, CancellationToken cancellationToken = default);

    // ── 0.3 ─────────────────────────────────────────────────────────────────
    // New members have default implementations so existing stores keep compiling; the EF Core, MongoDB and in-memory
    // stores implement them natively.

    /// <summary>Find several entries by id (missing ids are skipped). Use it to refresh <c>StoredFileRef</c> copies after transfers.</summary>
    async Task<IReadOnlyList<IFileEntry>> FindManyAsync(IEnumerable<DefaultIdType> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var found = new List<IFileEntry>();
        foreach (var id in ids.Distinct())
        {
            if (await FindAsync(id, cancellationToken).ConfigureAwait(false) is { } entry)
                found.Add(entry);
        }

        return found;
    }

    /// <summary>
    /// Find a non-deleted entry by location within one instance (<paramref name="storageName"/> <c>null</c> searches every
    /// instance, like <see cref="FindByLocationAsync(string, CancellationToken)"/>).
    /// </summary>
    async Task<IFileEntry?> FindByLocationAsync(string fileLocation, string? storageName, CancellationToken cancellationToken = default)
    {
        var entry = await FindByLocationAsync(fileLocation, cancellationToken).ConfigureAwait(false);
        return entry is null || storageName is null || StorageInstance.AreSame(entry.StorageName, storageName) ? entry : null;
    }

    /// <summary>
    /// <see cref="ListOrphansAsync(DateTimeOffset, int, CancellationToken)"/> limited to one instance
    /// (<paramref name="storageName"/> <c>null</c>: all). Previous versions kept by <c>ReplaceOptions.KeepPreviousVersion</c>
    /// are excluded unless <paramref name="includeSupersededVersions"/>.
    /// </summary>
    async Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(
        DateTimeOffset uploadedBefore, int maxCount, string? storageName, bool includeSupersededVersions = false, CancellationToken cancellationToken = default)
    {
        var orphans = await ListOrphansAsync(uploadedBefore, maxCount, cancellationToken).ConfigureAwait(false);
        return orphans.Where(o => (storageName is null || StorageInstance.AreSame(o.StorageName, storageName))
                                  && (includeSupersededVersions || o.SupersededById is null)).ToList();
    }

    /// <summary>Query entries with filters, sorting and paging.</summary>
    Task<FileEntryPage> QueryAsync(FileEntryQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support QueryAsync.");

    /// <summary>File count and bytes, grouped by the requested dimensions and computed by the database.</summary>
    Task<IReadOnlyList<FileUsage>> GetUsageAsync(FileUsageQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support GetUsageAsync.");

    /// <summary>Groups of non-deleted originals with the same checksum (per instance unless requested otherwise).</summary>
    Task<FileDuplicatePage> FindDuplicatesAsync(FileDuplicateQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support FindDuplicatesAsync.");

    /// <summary>Shorthand for <see cref="FindDuplicatesAsync(FileDuplicateQuery, CancellationToken)"/>.</summary>
    Task<FileDuplicatePage> FindDuplicatesAsync(string? storageName, string? category, int minCount = 2, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
        => FindDuplicatesAsync(new FileDuplicateQuery { StorageName = storageName, Category = category, MinCount = minCount, Skip = skip, Take = take }, cancellationToken);

    /// <summary>
    /// Re-point entries at bytes that were moved to another instance, keeping their ids and links (so every
    /// <c>FileEntryId</c> reference stays valid). All relocations are applied atomically where the database allows it.
    /// </summary>
    Task RelocateAsync(IReadOnlyCollection<FileRelocation> relocations, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support RelocateAsync.");

    /// <summary>Re-point one entry; see <see cref="RelocateAsync(IReadOnlyCollection{FileRelocation}, CancellationToken)"/>.</summary>
    Task RelocateAsync(
        DefaultIdType id, string storageName, string newLocation, string? newStoragePath, bool encrypted,
        string? storageProvider = null, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => RelocateAsync([new FileRelocation(id, storageName, newLocation, newStoragePath, encrypted, storageProvider, metadata)], cancellationToken);

    /// <summary>
    /// Mark <paramref name="previousId"/> as superseded by <paramref name="newId"/>: the previous entry is kept (and no
    /// longer an orphan candidate), its active links move to the new entry, and the new entry gets
    /// <see cref="IFileEntry.PreviousVersionId"/> and the next <see cref="IFileEntry.VersionNumber"/>.
    /// </summary>
    Task SupersedeAsync(DefaultIdType previousId, DefaultIdType newId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support SupersedeAsync.");

    /// <summary>Every non-deleted version in the chain of <paramref name="id"/>, newest first (empty when the id is unknown).</summary>
    async Task<IReadOnlyList<IFileEntry>> ListVersionsAsync(DefaultIdType id, CancellationToken cancellationToken = default)
    {
        var current = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return [];

        var seen = new HashSet<DefaultIdType> { current.Id };
        while (current.SupersededById is { } next && seen.Add(next)
               && await FindAsync(next, cancellationToken).ConfigureAwait(false) is { } newer)
            current = newer;

        var versions = new List<IFileEntry> { current };
        seen = [current.Id];
        while (current.PreviousVersionId is { } previous && seen.Add(previous)
               && await FindAsync(previous, cancellationToken).ConfigureAwait(false) is { } older)
        {
            versions.Add(older);
            current = older;
        }

        // Purged versions stay in the chain (so it is not broken) but are not returned.
        return versions.Where(v => !v.IsDeleted).ToList();
    }
}
