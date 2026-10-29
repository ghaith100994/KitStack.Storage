namespace KitStack.Abstractions.Security;

/// <summary>
/// Encrypts file content before a provider stores it and decrypts it when it is read back.
/// The default implementation is <see cref="AesGcmFileContentEncryptor"/>, enabled by
/// <c>StorageOptions.MasterEncryptionKey</c>. Register your own implementation (for example one backed
/// by a key vault) as a singleton to replace it.
/// </summary>
public interface IFileContentEncryptor
{
    /// <summary>Number of leading bytes <see cref="IsEncrypted"/> needs.</summary>
    int HeaderLength { get; }

    /// <summary>True when <paramref name="header"/> is the start of content produced by this encryptor.</summary>
    bool IsEncrypted(ReadOnlySpan<byte> header);

    /// <summary>Read <paramref name="plaintext"/> to the end and write the encrypted form to <paramref name="destination"/>.</summary>
    Task EncryptAsync(Stream plaintext, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Read <paramref name="ciphertext"/> to the end and write the decrypted form to <paramref name="destination"/>.</summary>
    /// <exception cref="Exceptions.StorageException">The content was tampered with, truncated, or encrypted with an unknown key.</exception>
    Task DecryptAsync(Stream ciphertext, Stream destination, CancellationToken cancellationToken = default);
}
