using System;
using System.Collections.Generic;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Represents metadata for a file stored by a provider.
/// Implementers (providers or application models) should follow this contract.
/// </summary>
public interface IFileEntry
{
    DefaultIdType Id { get; set; }

    /// <summary>
    /// Original file name (including extension).
    /// </summary>
    string FileName { get; set; }

    /// <summary>
    /// Logical or provider-specific file location / key (for example: "app/images/..." or S3 key).
    /// </summary>
    string FileLocation { get; set; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    long Size { get; set; }

    /// <summary>
    /// Optional MIME/content type.
    /// </summary>
    string? ContentType { get; set; }

    /// <summary>
    /// Additional metadata (provider-agnostic).
    /// </summary>
    IDictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// When the file was uploaded.
    /// </summary>
    DateTimeOffset UploadedTime { get; set; }
}