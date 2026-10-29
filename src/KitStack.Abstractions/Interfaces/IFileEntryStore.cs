namespace KitStack.Abstractions.Interfaces;

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
    /// Image variants are not returned on their own: they are removed together with their original.
    /// </summary>
    Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(DateTimeOffset uploadedBefore, int maxCount, CancellationToken cancellationToken = default);
}
