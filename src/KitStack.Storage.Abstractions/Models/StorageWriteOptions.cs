using KitStack.Storage.Abstractions.Options;

namespace KitStack.Storage.Abstractions.Models;

/// <summary>
/// Describes a file to create with the non-generic <c>IFileStorageManager.CreateAsync(StorageWriteOptions, Stream, ...)</c>.
/// </summary>
public sealed class StorageWriteOptions
{
    /// <summary>Original file name including extension (used for the stored name and content-type resolution). Required.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Content type declared by the caller. Recorded in <c>Metadata["DeclaredContentType"]</c> but never trusted:
    /// the entry's content type is resolved from the bytes.
    /// </summary>
    public string? DeclaredContentType { get; set; }

    /// <summary>
    /// Logical category or module. Recorded on the entry, and used as the first folder level when
    /// <see cref="Folder"/> is not set (then it is required).
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Logical entity name used as the second folder level when <see cref="Folder"/> is not set
    /// (the generic overloads pass <c>typeof(T).Name</c>). Required when <see cref="Folder"/> is not set.
    /// </summary>
    public string? EntityName { get; set; }

    /// <summary>
    /// Explicit relative folder, for example <c>hrm/employees/documents</c>. When set it is used verbatim (after
    /// sanitization; absolute paths and <c>.</c>/<c>..</c> segments are rejected) instead of
    /// <c>{category}/{entity}/{typeFolder}</c>.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    /// Optional per-call upload policy. It can only make the instance's policy stricter: a file must satisfy both,
    /// and the smaller size limit wins.
    /// </summary>
    public UploadPolicy? Policy { get; set; }

    /// <summary>Extra metadata copied onto the entry. Keys the library uses itself (see <see cref="FileEntryMetadataKeys"/>) are overwritten.</summary>
    public IDictionary<string, string>? Metadata { get; set; }

    /// <summary>Optional link recorded on <c>IFileEntry.RelatedEntities</c> (and on the variants).</summary>
    public StorageEntityLink? Link { get; set; }
}

/// <summary>A link between a new file and a domain entity.</summary>
public sealed class StorageEntityLink
{
    /// <summary>Logical entity name.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Entity identifier in invariant string form.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Optional notes.</summary>
    public string? Notes { get; set; }
}

/// <summary>Metadata keys written by the library.</summary>
public static class FileEntryMetadataKeys
{
    /// <summary>Content type declared by the uploader.</summary>
    public const string DeclaredContentType = "DeclaredContentType";

    /// <summary>On variants: id ("N" format) of the original.</summary>
    public const string OriginalId = "OriginalId";

    /// <summary>On originals: location of the compressed variant.</summary>
    public const string CompressedPath = "CompressedPath";

    /// <summary>On originals: location of the thumbnail.</summary>
    public const string ThumbnailPath = "ThumbnailPath";

    /// <summary>On originals: ';'-separated locations of additional sizes.</summary>
    public const string Variants = "Variants";

    /// <summary>Logical relative folder the file was written to (provider prefixes excluded). Used by transfers and restores.</summary>
    public const string Folder = "Folder";
}
