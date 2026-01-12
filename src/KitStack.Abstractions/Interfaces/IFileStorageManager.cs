using Microsoft.AspNetCore.Http;

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
    //Task CreateAsync(IFileEntry fileEntry, Stream content, CancellationToken cancellationToken = default);

    ///// <summary>
    ///// Read the file content into a byte array.
    ///// </summary>
    //Task<byte[]> ReadAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    ///// <summary>
    ///// Open a read stream for the file content.
    ///// Caller is responsible for disposing the returned stream.
    ///// </summary>
    //Task<Stream> ReadAsStreamAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    ///// <summary>
    ///// Delete the file referenced by fileEntry.
    ///// </summary>
    //Task DeleteAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    ///// <summary>
    ///// Mark or move the file into archive tier (provider-defined).
    ///// </summary>
    //Task ArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    ///// <summary>
    ///// Restore the file from archive tier (provider-defined).
    ///// </summary>
    //Task UnArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// High-level convenience: upload an IFormFile for entity T into the configured local store.
    /// If the file is an image and image-processing options are enabled, the manager will create
    /// variants (thumbnail, compressed, additional sizes) according to configuration.
    /// Returns the provider-relative path of the primary stored file (suitable for storing in DB).
    /// </summary>
    //Task<string> UploadAsync<T>(IFormFile? file, string? category, CancellationToken cancellationToken = default)
    //    where T : class;

    /// <summary>
    /// High-level helper: create and store a file associated with the given entity.
    /// Returns a populated IFileEntry describing the stored file (primary/original).
    /// - The implementation SHOULD NOT mutate the entity by default, but if the entity exposes
    ///   a compatible method (e.g. AddFileAttachment(FileEntry)) the provider MAY call it.
    /// - Image variants (thumbnail/compressed/other sizes) are created according to provider options.
    /// </summary>
    Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class;

    Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
    where T : class;
}