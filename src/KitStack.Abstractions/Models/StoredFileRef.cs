using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Utilities;

namespace KitStack.Abstractions.Models;

/// <summary>
/// Value object that a domain entity stores to point at a file, instead of repeating
/// FileEntryId / location / MIME type / size / checksum columns on every entity.
/// Map it as an EF Core owned type with <c>OwnsStoredFile(...)</c> from KitStack.Storage.EntityFrameworkCore.
/// </summary>
public sealed record StoredFileRef
{
    /// <summary>Id of the <see cref="IFileEntry"/> this reference points at.</summary>
    public DefaultIdType FileEntryId { get; init; }

    /// <summary>Provider-relative location (<see cref="IFileEntry.FileLocation"/>).</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>File name shown to users.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Resolved content type.</summary>
    public string ContentType { get; init; } = ContentTypes.OctetStream;

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Lower-case hex SHA-256 of the content.</summary>
    public string? Checksum { get; init; }

    /// <summary>Provider that stored the file (Local, S3, Azure, ...).</summary>
    public string? StorageProvider { get; init; }

    /// <summary>Whether the content is encrypted at rest.</summary>
    public bool Encrypted { get; init; }

    /// <summary>Create a reference from a stored entry.</summary>
    public static StoredFileRef From(IFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new StoredFileRef
        {
            FileEntryId = entry.Id,
            Location = entry.FileLocation,
            FileName = entry.FileName,
            ContentType = entry.ContentType,
            Size = entry.Size,
            Checksum = entry.Checksum,
            StorageProvider = entry.StorageProvider,
            Encrypted = entry.Encrypted,
        };
    }

    /// <summary>
    /// Rebuild a minimal <see cref="IFileEntry"/> that storage managers accept for
    /// <c>OpenReadAsync</c>, <c>ExistsAsync</c>, <c>DeleteAsync</c> and <c>GetUrlAsync</c>.
    /// </summary>
    public FileEntry ToFileEntry() => new()
    {
        Id = FileEntryId,
        FileLocation = Location,
        FileName = FileName,
        OriginalFileName = FileName,
        ContentType = ContentType,
        Size = Size,
        Checksum = Checksum,
        StorageProvider = StorageProvider,
        Encrypted = Encrypted,
        FileExtension = Path.GetExtension(FileName).ToLowerInvariant(),
        VariantType = "original",
        LastAccessedTime = null,
        RelatedEntities = [],
    };
}
