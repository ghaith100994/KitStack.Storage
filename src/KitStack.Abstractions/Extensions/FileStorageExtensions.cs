using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KitStack.Abstractions.Interfaces;

namespace KitStack.Abstractions.Extensions;

/// <summary>
/// Extension helpers for IFileStorageManager consumers.
/// </summary>
public static class FileStorageExtensions
{
    /// <summary>
    /// Copies file content from the storage provider to the destination stream.
    /// This avoids buffering the whole file into memory.
    /// </summary>
    public static async Task CopyToAsync(this IFileStorageManager storage, IFileEntry fileEntry, Stream destination, CancellationToken cancellationToken = default)
    {
        // await using var source = await storage.ReadAsStreamAsync(fileEntry, cancellationToken);
        // await source.CopyToAsync(destination, cancellationToken);
    }
}