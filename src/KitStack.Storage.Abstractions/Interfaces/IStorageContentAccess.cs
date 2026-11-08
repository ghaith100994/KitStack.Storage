namespace KitStack.Storage.Abstractions.Interfaces;

/// <summary>
/// Low-level access to a storage instance's bytes, without creating or tracking <see cref="IFileEntry"/> metadata.
/// Used to move content between instances (<c>IFileTransferService</c>). Implemented by <c>FileStorageManagerBase</c>
/// and forwarded by the library's decorators.
/// </summary>
public interface IStorageContentAccess
{
    /// <summary>Name of the storage instance.</summary>
    string StorageName { get; }

    /// <summary>
    /// The location <see cref="PutContentAsync"/> would return for these arguments, or <c>null</c> when the provider
    /// cannot tell in advance. Used to detect content that was already copied (resumable transfers).
    /// </summary>
    string? PredictLocation(string folder, string fileName, string variantType);

    /// <summary>
    /// Store <paramref name="plaintext"/> at <paramref name="request"/>'s folder and file name (both sanitized), encrypted
    /// with this instance's key when encryption is on. An existing file at that location is replaced.
    /// </summary>
    Task<StoredContent> PutContentAsync(StorageContentWrite request, Stream plaintext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the bytes at <paramref name="location"/> (decrypting them when needed) and describe them: plaintext size and
    /// checksum, and whether they are stored encrypted. <c>null</c> when nothing is stored there.
    /// </summary>
    Task<StoredContent?> VerifyContentAsync(string location, string variantType, CancellationToken cancellationToken = default);

    /// <summary>Delete the bytes at <paramref name="location"/> (no variants, no metadata). Missing files are not an error.</summary>
    Task DeleteContentAsync(string location, string variantType, CancellationToken cancellationToken = default);
}

/// <summary>Arguments of <see cref="IStorageContentAccess.PutContentAsync"/>.</summary>
/// <param name="Folder">Relative logical folder.</param>
/// <param name="FileName">Stored file name.</param>
/// <param name="ContentType">Content type of the plaintext.</param>
/// <param name="VariantType"><c>original</c> or a variant type; providers may route on it.</param>
public sealed record StorageContentWrite(string Folder, string FileName, string ContentType, string VariantType = "original");

/// <summary>Result of <see cref="IStorageContentAccess.PutContentAsync"/>.</summary>
/// <param name="Location">Provider location (<see cref="IFileEntry.FileLocation"/>).</param>
/// <param name="StoragePath">Value for <see cref="IFileEntry.StoragePath"/>.</param>
/// <param name="Size">Plaintext size in bytes.</param>
/// <param name="Checksum">Lower-case hex SHA-256 of the plaintext.</param>
/// <param name="Encrypted">Whether the bytes were stored encrypted.</param>
/// <param name="StorageProvider">Provider identifier (Local, S3, ...).</param>
public sealed record StoredContent(string Location, string? StoragePath, long Size, string Checksum, bool Encrypted, string StorageProvider);
