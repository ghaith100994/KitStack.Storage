namespace KitStack.Abstractions.Models;

/// <summary>
/// Persistent storage provider entity.
/// Stores provider metadata and serialized options.
/// </summary>
public class StorageProvider
{
    public DefaultIdType Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Logical name (e.g. "Local", "S3", "Sftp")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// ProviderType stored as string (e.g. "Local", "S3", "Sftp"). Prefer using StorageProviderType constants.
    /// </summary>
    public string? ProviderType { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// When true, this provider will be used when providerId is omitted.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Arbitrary provider-specific options object (LocalOptions, S3Options, SftpOptions, etc.).
    /// Keep typed on registration so registry consumers can TryGetOptions<T>.
    /// </summary>
    public object? Options { get; set; }

    /// <summary>
    /// The CLR type name of the Options object (assembly-qualified or short name).
    /// Used to help deserialize OptionsJson into the correct type.
    /// </summary>
    public string? OptionsType { get; set; }

    /// <summary>
    /// Assembly-qualified name of the concrete manager type (optional).
    /// e.g. "KitStack.Storage.S3.Services.S3FileStorageManager, KitStack.Storage.S3"
    /// </summary>
    public string? ManagerType { get; set; }

    /// <summary>
    /// Optional reference (name/URI/identifier) to the provider's master encryption key stored in a secrets manager or KMS.
    /// Prefer storing a reference rather than raw key bytes in application configuration.
    /// </summary>
    public string? EncryptionKeyReference { get; set; }
}
