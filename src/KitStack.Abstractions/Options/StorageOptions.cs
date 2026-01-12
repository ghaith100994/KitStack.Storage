namespace KitStack.Abstractions.Options;

/// <summary>
/// Top-level storage configuration options bound from configuration (Storage section).
/// Keep provider-specific detailed options in provider projects if possible.
/// </summary>
public class StorageOptions
{
    /// <summary>
    /// Provider name, e.g. "Local", "Azure", "Amazon", "Fake", "Google".
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Optional master encryption key for providers that support encryption-at-rest.
    /// Base64 or other formats are allowed depending on your implementation.
    /// </summary>
    public string? MasterEncryptionKey { get; set; }
}
