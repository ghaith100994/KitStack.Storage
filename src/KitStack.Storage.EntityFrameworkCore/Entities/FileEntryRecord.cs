using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Utilities;

namespace KitStack.Storage.EntityFrameworkCore.Entities;

/// <summary>
/// Database row for a stored file (table <c>FileEntries</c>); its entity links live in <c>FileEntryLinks</c>.
/// </summary>
public class FileEntryRecord : IFileEntry
{
    public DefaultIdType Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileLocation { get; set; } = string.Empty;
    public string? StoragePath { get; set; }
    public string? Category { get; set; }
    public long Size { get; set; }
    public string ContentType { get; set; } = ContentTypes.OctetStream;
    public string? Checksum { get; set; }
    public string? FileExtension { get; set; }
    public IDictionary<string, string>? Metadata { get; set; }
    public DateTimeOffset UploadedTime { get; set; }
    public string? VariantType { get; set; }
    public bool Encrypted { get; set; }
    public string? OriginalFileName { get; set; }
    public string? StorageProvider { get; set; }
    public DateTimeOffset? LastAccessedTime { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>When the file was marked deleted.</summary>
    public DateTimeOffset? DeletedTime { get; set; }

    /// <summary>For image variants: the id of the original they were generated from.</summary>
    public DefaultIdType? ParentId { get; set; }

    public ICollection<FileRelatedEntity>? RelatedEntities { get; set; } = [];

    /// <summary>Copy an <see cref="IFileEntry"/> into a new record.</summary>
    public static FileEntryRecord From(IFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DefaultIdType? parentId = entry.Metadata is { } md && md.TryGetValue("OriginalId", out var raw) && Guid.TryParse(raw, out var parsed)
            ? parsed
            : null;

        return new FileEntryRecord
        {
            Id = entry.Id,
            FileName = entry.FileName,
            FileLocation = entry.FileLocation,
            StoragePath = entry.StoragePath,
            Category = entry.Category,
            Size = entry.Size,
            ContentType = entry.ContentType,
            Checksum = entry.Checksum,
            FileExtension = entry.FileExtension,
            Metadata = entry.Metadata is null ? null : new Dictionary<string, string>(entry.Metadata),
            UploadedTime = entry.UploadedTime,
            VariantType = entry.VariantType,
            Encrypted = entry.Encrypted,
            OriginalFileName = entry.OriginalFileName,
            StorageProvider = entry.StorageProvider,
            LastAccessedTime = entry.LastAccessedTime,
            IsDeleted = entry.IsDeleted,
            ParentId = parentId,
            RelatedEntities = entry.RelatedEntities?.Select(r => new FileRelatedEntity
            {
                EntityId = r.EntityId,
                EntityName = r.EntityName,
                IsUsed = r.IsUsed,
                Notes = r.Notes,
                LinkedOn = r.LinkedOn,
            }).ToList() ?? [],
        };
    }
}
