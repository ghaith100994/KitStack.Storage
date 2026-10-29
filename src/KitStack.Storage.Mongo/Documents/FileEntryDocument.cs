using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Utilities;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace KitStack.Storage.Mongo.Documents;

/// <summary>A stored file as kept in MongoDB; links are embedded.</summary>
public sealed class FileEntryDocument
{
    [BsonId]
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileLocation { get; set; } = string.Empty;
    public string? StoragePath { get; set; }
    public string? Category { get; set; }
    public long Size { get; set; }
    public string ContentType { get; set; } = ContentTypes.OctetStream;
    public string? Checksum { get; set; }
    public string? FileExtension { get; set; }

    // Arbitrary keys: stored as [key, value] pairs so '.' or '$' in a key is never a problem.
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfArrays)]
    public Dictionary<string, string>? Metadata { get; set; }

    // UTC BSON dates (not DateTimeOffset's array form) so range queries and sorting work.
    public DateTime UploadedTime { get; set; }
    public string? VariantType { get; set; }
    public bool Encrypted { get; set; }
    public string? OriginalFileName { get; set; }
    public string? StorageProvider { get; set; }
    public DateTime? LastAccessedTime { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedTime { get; set; }

    /// <summary>For image variants: the id of the original they were generated from.</summary>
    public Guid? ParentId { get; set; }

    public List<FileLinkDocument> Links { get; set; } = [];

    public static FileEntryDocument From(IFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new FileEntryDocument
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
            UploadedTime = entry.UploadedTime.UtcDateTime,
            VariantType = entry.VariantType,
            Encrypted = entry.Encrypted,
            OriginalFileName = entry.OriginalFileName,
            StorageProvider = entry.StorageProvider,
            LastAccessedTime = entry.LastAccessedTime?.UtcDateTime,
            IsDeleted = entry.IsDeleted,
            ParentId = entry.Metadata is { } md && md.TryGetValue("OriginalId", out var raw) && Guid.TryParse(raw, out var parent) ? parent : null,
            Links = entry.RelatedEntities?.Select(r => new FileLinkDocument
            {
                EntityName = r.EntityName,
                EntityId = r.EntityId,
                IsUsed = r.IsUsed,
                Notes = r.Notes,
                LinkedOn = r.LinkedOn.UtcDateTime,
            }).ToList() ?? [],
        };
    }

    public FileEntry ToFileEntry() => new()
    {
        Id = Id,
        FileName = FileName,
        FileLocation = FileLocation,
        StoragePath = StoragePath,
        Category = Category,
        Size = Size,
        ContentType = ContentType,
        Checksum = Checksum,
        FileExtension = FileExtension,
        Metadata = Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(Metadata),
        UploadedTime = new DateTimeOffset(DateTime.SpecifyKind(UploadedTime, DateTimeKind.Utc)),
        VariantType = VariantType,
        Encrypted = Encrypted,
        OriginalFileName = OriginalFileName,
        StorageProvider = StorageProvider,
        LastAccessedTime = LastAccessedTime is { } accessed ? new DateTimeOffset(DateTime.SpecifyKind(accessed, DateTimeKind.Utc)) : null,
        IsDeleted = IsDeleted,
        RelatedEntities = Links.Select(l => new FileRelatedEntity
        {
            EntityName = l.EntityName,
            EntityId = l.EntityId,
            IsUsed = l.IsUsed,
            Notes = l.Notes,
            LinkedOn = new DateTimeOffset(DateTime.SpecifyKind(l.LinkedOn, DateTimeKind.Utc)),
        }).ToList(),
    };
}

/// <summary>A link between a file and an entity, embedded in <see cref="FileEntryDocument.Links"/>.</summary>
public sealed class FileLinkDocument
{
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public bool IsUsed { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime LinkedOn { get; set; }
}
