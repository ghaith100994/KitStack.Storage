namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Abstraction for file storage operations.
/// Implementations should be lightweight and provider-specific.
/// </summary>
public interface IFileStorageManager
{
    /// <summary>
    /// Create / upload content for the given file entry.
    /// </summary>
    Task CreateAsync(IFileEntry fileEntry, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the file content into a byte array.
    /// </summary>
    Task<byte[]> ReadAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a read stream for the file content.
    /// Caller is responsible for disposing the returned stream.
    /// </summary>
    Task<Stream> ReadAsStreamAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the file referenced by fileEntry.
    /// </summary>
    Task DeleteAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark or move the file into archive tier (provider-defined).
    /// </summary>
    Task ArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restore the file from archive tier (provider-defined).
    /// </summary>
    Task UnArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);
}