namespace KitStack.Abstractions.Options;

/// <summary>
/// Per-instance overrides of the global <see cref="StorageOptions"/> (bound from <c>Storage:Instances:{name}</c> for
/// statically registered instances, or carried by a <c>StorageInstanceDefinition</c>). Unset values inherit the global ones.
/// </summary>
public sealed class StorageOptionsOverrides
{
    /// <summary>Upload policy of the instance. <c>null</c> inherits the global <see cref="StorageOptions.Upload"/>.</summary>
    public UploadPolicy? Upload { get; set; }

    /// <summary>Base64 master key of the instance. <c>null</c> inherits the global key (unless <see cref="DisableEncryption"/>).</summary>
    public string? MasterEncryptionKey { get; set; }

    /// <summary>Previous keys of the instance (decryption only). Added to the global previous keys.</summary>
    public IList<string> PreviousEncryptionKeys { get; } = [];

    /// <summary>Store new files of this instance in plaintext even when a global key is configured. Existing encrypted files stay readable.</summary>
    public bool DisableEncryption { get; set; }

    /// <summary>
    /// Merge <paramref name="overrides"/> over <paramref name="global"/> into a new <see cref="StorageOptions"/> for
    /// the instance <paramref name="instanceName"/>. <paramref name="global"/> is not modified.
    /// </summary>
    public static StorageOptions Merge(StorageOptions? global, StorageOptionsOverrides? overrides, string instanceName)
    {
        global ??= new StorageOptions();
        var merged = new StorageOptions
        {
            Provider = global.Provider,
            InstanceName = instanceName,
            Upload = Clone(overrides?.Upload ?? global.Upload ?? new UploadPolicy()),
        };

        // Keys that can decrypt existing files of the instance: its own current key is the master key; the
        // global key (when replaced or disabled) and every previous key stay usable for reading.
        string? master;
        if (overrides?.DisableEncryption == true)
            master = null;
        else
            master = string.IsNullOrWhiteSpace(overrides?.MasterEncryptionKey) ? global.MasterEncryptionKey : overrides.MasterEncryptionKey;
        merged.MasterEncryptionKey = master;

        var previous = new List<string>();
        if (!string.IsNullOrWhiteSpace(global.MasterEncryptionKey) && global.MasterEncryptionKey != master)
            previous.Add(global.MasterEncryptionKey);
        previous.AddRange(overrides?.PreviousEncryptionKeys ?? []);
        previous.AddRange(global.PreviousEncryptionKeys);
        foreach (var key in previous.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal))
            merged.PreviousEncryptionKeys.Add(key);

        // A plaintext instance (no master key) still decrypts existing files with the previous keys (FileStorageManagerBase).
        return merged;
    }

    /// <summary>Copy of <paramref name="policy"/>.</summary>
    public static UploadPolicy Clone(UploadPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var copy = new UploadPolicy { MaxBytes = policy.MaxBytes };
        foreach (var type in policy.AllowedContentTypes)
            copy.AllowedContentTypes.Add(type);
        foreach (var extension in policy.AllowedExtensions)
            copy.AllowedExtensions.Add(extension);
        return copy;
    }
}
