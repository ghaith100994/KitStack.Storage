using System;
using KitStack.Abstractions.Interfaces;

namespace KitStack.Fakes.Models;

/// <summary>
/// In-memory representation of a stored file for the fake provider.
/// </summary>
public sealed class FakeStoredFile(IFileEntry fileEntry, byte[] content)
{
    public IFileEntry FileEntry { get; init; } = fileEntry;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "<Pending>")]
    public byte[] Content => content;

    public bool IsArchived { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
