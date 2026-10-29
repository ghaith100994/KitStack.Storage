namespace KitStack.Abstractions.Options;

/// <summary>
/// Top-level storage configuration options bound from configuration (Storage section).
/// Keep provider-specific detailed options in provider projects if possible.
/// </summary>
public class StorageOptions
{
    /// <summary>
    /// Provider name, e.g. "Local", "S3", "Azure", "Sftp", "Ftp", "Fake".
    /// Read by <c>KitStackStorageBuilder.Provider</c> so applications can pick a provider from configuration.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Base64 master key for encryption at rest (at least 16 bytes once decoded; 32 recommended).
    /// When set, every file written from now on is encrypted with AES-256-GCM (a per-file key is
    /// derived from this key with HKDF) and <c>IFileEntry.Encrypted</c> is set. Reads decrypt transparently.
    /// When empty, files are stored in plaintext.
    /// </summary>
    public string? MasterEncryptionKey { get; set; }

    /// <summary>
    /// Base64 keys that were previously used as <see cref="MasterEncryptionKey"/>.
    /// They are only used to decrypt existing files, which allows key rotation.
    /// </summary>
    public IList<string> PreviousEncryptionKeys { get; } = [];

    /// <summary>Upload rules enforced before any provider is called.</summary>
    public UploadPolicy Upload { get; set; } = new();
}
