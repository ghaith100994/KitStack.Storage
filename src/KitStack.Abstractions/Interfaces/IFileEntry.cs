using System;
using System.Collections.Generic;
using KitStack.Abstractions.Models;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Represents metadata for a file stored by a provider.
/// Implementers (providers or application models) should follow this contract.
/// </summary>
public interface IFileEntry
{
    /// <summary>
    /// Identifier for the file entry. The concrete type is provider/application-specific.
    /// </summary>
    DefaultIdType Id { get; set; }

    /// <summary>
    /// Original file name including extension (for example "photo.jpg").
    /// </summary>
    string FileName { get; set; }

    /// <summary>
    /// Logical or provider-specific location/key for the stored file (for example "app/images/2026/photo.jpg" or an S3 key).
    /// This value is intended to be persisted by callers and used to retrieve the file from the provider.
    /// </summary>
    string FileLocation { get; set; }

    /// <summary>
    /// Optional logical category or module name used to group files (for example "Users" or "Products").
    /// Providers may use this value to organize storage layout.
    /// </summary>
    string? Category { get; set; }

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    long Size { get; set; }

    /// <summary>
    /// Optional MIME/content type (for example "image/jpeg").
    /// </summary>
    string? ContentType { get; set; }

    /// <summary>
    /// Optional file extension including the leading dot (for example ".jpg").
    /// </summary>
    string? FileExtension { get; set; }

    /// <summary>
    /// Provider-agnostic key/value metadata attached to this file.
    /// Providers may store additional information such as variant paths here.
    /// </summary>
    IDictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// When the file was uploaded (UTC).
    /// </summary>
    DateTimeOffset UploadedTime { get; set; }

    /// <summary>
    /// Optional variant classification for this entry (for example "original", "thumbnail", "compressed", "small").
    /// Providers should set this for variant files to make querying and display easier.
    /// </summary>
    string? VariantType { get; set; }

    /// <summary>
    /// Indicates whether the file content is stored encrypted by the provider.
    /// </summary>
    bool Encrypted { get; set; }

    /// <summary>
    /// Original file name provided by the uploader before sanitization.
    /// </summary>
    string? OriginalFileName { get; set; }

    /// <summary>
    /// Logical storage provider identifier (Local, S3, etc.).
    /// </summary>
    string? StorageProvider { get; set; }

    /// <summary>
    /// When the file was last accessed/read.
    /// </summary>
    DateTimeOffset? LastAccessedTime { get; set; }

    /// <summary>
    /// Indicates whether the file has been soft-deleted.
    /// </summary>
    bool IsDeleted { get; set; }

    /// <summary>
    /// Optional relationships linking this file to one or more domain entities.
    /// </summary>
    ICollection<FileRelatedEntity>? RelatedEntities { get; set; }
}
