using System;
using System.Collections.Generic;
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

    public long Size { get; set; }

    public string? ContentType { get; set; }

    public IDictionary<string, string>? Metadata { get; set; }

    public DateTimeOffset UploadedTime { get; set; } = DateTimeOffset.UtcNow;
}