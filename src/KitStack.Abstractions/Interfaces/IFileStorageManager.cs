using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Models;
using Microsoft.AspNetCore.Http;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Abstraction for file storage operations.
/// Implementations should be lightweight and provider-specific.
/// </summary>
public interface IFileStorageManager
{
    /// <summary>
    /// Create and store a file for the specified entity type <typeparamref name="T"/>.
    /// The implementation should produce a populated <see cref="IFileEntry"/> describing
    /// the stored primary/original file. Providers may also create image variants according
    /// to their configuration but this method returns the primary entry only.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the stored <see cref="IFileEntry"/>.</returns>
    /// <exception cref="StorageValidationException">The category is missing or the file violates the upload policy.</exception>
    Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Create and store a file from a raw stream (background jobs, imports, generated documents).
    /// The stream is read to the end but not disposed.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="content">File content.</param>
    /// <param name="fileName">Original file name including extension (used for the stored name and content-type resolution).</param>
    /// <param name="contentType">
    /// Content type declared by the caller. It is recorded in <see cref="IFileEntry.Metadata"/> as
    /// <c>DeclaredContentType</c> but not trusted: <see cref="IFileEntry.ContentType"/> is resolved from the bytes.
    /// </param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="StorageValidationException">The category is missing or the file violates the upload policy.</exception>
    Task<IFileEntry> CreateAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Create and store a file associated with the provided entity instance.
    /// Implementations may attach or mutate the entity (for example adding a FileEntry)
    /// when the entity implements <see cref="IFileAttachable"/>. Implementations should also
    /// record a relationship on the returned <see cref="IFileEntry"/> via <c>RelatedEntities</c>.
    /// </summary>
    /// <typeparam name="T">Entity type which implements <see cref="IFileAttachable"/>.</typeparam>
    /// <param name="entity">The entity instance to associate the file with.</param>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the stored <see cref="IFileEntry"/>.</returns>
    Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class, IFileAttachable;

    /// <summary>
    /// Create the primary/original file and any configured image variants (thumbnail, compressed,
    /// or additional sizes). Returns the primary <see cref="IFileEntry"/> and a list of variant
    /// entries that were created by the provider.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to a tuple containing the primary entry and created variants.</returns>
    Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Stream overload of <see cref="CreateWithVariantsAsync{T}(IFormFile,string?,CancellationToken)"/>.
    /// </summary>
    Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Open the stored content of <paramref name="entry"/> for reading. The caller owns (and must dispose) the stream.
    /// Encrypted content is decrypted transparently.
    /// </summary>
    /// <exception cref="StorageNotFoundException">The file does not exist in the provider.</exception>
    /// <exception cref="StorageConfigurationException">The file is encrypted and no matching key is configured.</exception>
    Task<Stream> OpenReadAsync(IFileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check whether the content of <paramref name="entry"/> exists in the provider.
    /// </summary>
    Task<bool> ExistsAsync(IFileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the content of <paramref name="entry"/>. When the entry is an original that has image variants
    /// recorded in its <see cref="IFileEntry.Metadata"/> (CompressedPath, ThumbnailPath, Variants) they are deleted too.
    /// Deleting a file that no longer exists is not an error. Sets <see cref="IFileEntry.IsDeleted"/>.
    /// </summary>
    Task DeleteAsync(IFileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the content stored at <paramref name="entry"/>'s existing location (restoring a backup, saving an
    /// edited document in place). The content goes through the upload size limit, content-type resolution,
    /// checksum and encryption like any upload, and <see cref="IFileEntry.Size"/>, <see cref="IFileEntry.Checksum"/>,
    /// <see cref="IFileEntry.ContentType"/> and <see cref="IFileEntry.Encrypted"/> are updated on the entry.
    /// Image variants are not regenerated.
    /// </summary>
    /// <exception cref="StorageValidationException">The content violates the upload policy.</exception>
    Task OverwriteAsync(IFileEntry entry, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a URL a client can use to download the file directly, or <c>null</c> when the provider cannot
    /// produce one (for example Local without a configured <c>PublicBaseUrl</c>, or encrypted content,
    /// which must be read through <see cref="OpenReadAsync"/>).
    /// </summary>
    /// <param name="entry">The stored file.</param>
    /// <param name="lifetime">How long a signed URL stays valid. <c>null</c> uses the provider default. Ignored by unsigned URLs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Uri?> GetUrlAsync(IFileEntry entry, TimeSpan? lifetime = null, CancellationToken cancellationToken = default);
}
