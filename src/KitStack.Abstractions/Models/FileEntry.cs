using KitStack.Abstractions.Interfaces;

namespace KitStack.Abstractions.Models;

/// <summary>
/// Simple concrete DTO that implements <see cref="IFileEntry"/>.
/// Use this in samples and tests; real systems may map their domain entities to IFileEntry.
/// </summary>
public class FileEntry : IFileEntry
{
    public DefaultIdType Id { get; set; } = Guid.NewGuid();

    public string FileName { get; set; } = string.Empty;

    public string FileLocation { get; set; } = string.Empty;

    public string? Category { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? ContentType { get; set; }

    public string? FileExtension { get; set; }

    public IDictionary<string, string>? Metadata { get; set; }

    public DateTimeOffset UploadedTime { get; set; } = DateTimeOffset.UtcNow;

    public string? VariantType { get; set; }

    public bool Encrypted { get; set; }

    public string? OriginalFileName { get; set; }

    public string? StorageProvider { get; set; }

    public DateTimeOffset? LastAccessedTime { get; set; } = DateTimeOffset.UtcNow;

    public bool IsDeleted { get; set; }

    public ICollection<FileRelatedEntity>? RelatedEntities { get; set; } = [];
}
