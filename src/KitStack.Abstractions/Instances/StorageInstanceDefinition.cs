using KitStack.Abstractions.Options;
using Microsoft.Extensions.Configuration;

namespace KitStack.Abstractions.Instances;

/// <summary>
/// A storage instance defined at runtime (for example a database row maintained by administrators), returned by an
/// <see cref="IStorageInstanceSource"/>. <see cref="Settings"/> bind to the provider's existing options type
/// (<c>LocalOptions</c>, <c>S3Options</c>, <c>AzureBlobOptions</c>, <c>SftpOptions</c>, <c>FtpOptions</c>, <c>FakeOptions</c>).
/// </summary>
public sealed class StorageInstanceDefinition
{
    private static readonly IConfiguration EmptySettings = new ConfigurationBuilder().Build();

    /// <summary>Create a definition.</summary>
    /// <param name="name">Instance name (case-insensitive, at most 100 characters).</param>
    /// <param name="providerType">Provider type: <c>Local</c>, <c>S3</c>, <c>Azure</c>, <c>Sftp</c>, <c>Ftp</c> or <c>Fake</c> (case-insensitive).</param>
    public StorageInstanceDefinition(string name, string providerType)
    {
        Name = StorageInstance.Validate(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        ProviderType = providerType.Trim();
    }

    /// <summary>Instance name.</summary>
    public string Name { get; }

    /// <summary>Provider type key of the <see cref="IStorageProviderFactory"/> that builds the instance.</summary>
    public string ProviderType { get; }

    /// <summary>Provider settings, using the same keys as the provider's configuration section (for example <c>MainTarget:BucketName</c>).</summary>
    public IConfiguration Settings { get; init; } = EmptySettings;

    /// <summary>Per-instance overrides of the global storage options (upload policy, encryption keys).</summary>
    public StorageOptionsOverrides? Storage { get; init; }

    /// <summary>
    /// Secrets by reference: setting path → reference resolved by <see cref="IStorageSecretResolver"/> when the instance
    /// is built (see <see cref="StorageSecretFields"/> for the well-known paths). Resolved values are never logged.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecretReferences { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Version or ETag of the definition. A cached instance is rebuilt when the source returns another value.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>Build <see cref="Settings"/> from flat key/value pairs (<c>"MainTarget:BucketName" = "erp-private"</c>).</summary>
    public static IConfiguration CreateSettings(IEnumerable<KeyValuePair<string, string?>> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>Name, provider type, version and the names of the secret references; never setting values.</summary>
    public override string ToString()
        => $"{Name} ({ProviderType}, version {Version ?? "-"}, secrets: [{string.Join(", ", SecretReferences.Keys)}])";
}

/// <summary>
/// Supplies runtime storage instance definitions (implemented by applications, for example over a database table).
/// Registered with <c>AddStorageInstanceSource&lt;T&gt;()</c> as a singleton: use a context factory or a scope inside.
/// </summary>
public interface IStorageInstanceSource
{
    /// <summary>Return the definition named <paramref name="name"/>, or <c>null</c> when it does not exist or is disabled.</summary>
    ValueTask<StorageInstanceDefinition?> FindAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Caching of instances built from an <see cref="IStorageInstanceSource"/>.</summary>
public sealed class StorageInstanceCacheOptions
{
    /// <summary>
    /// How long a built instance is used before the source is asked again whether its <c>Version</c> changed.
    /// <see cref="TimeSpan.Zero"/> asks on every resolution. Default 30 seconds. Call <c>Invalidate(name)</c> to apply
    /// an edit immediately.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Well-known setting paths for <see cref="StorageInstanceDefinition.SecretReferences"/>. Any setting path works.</summary>
public static class StorageSecretFields
{
    /// <summary>Prefix of paths applied to the instance's storage options instead of the provider settings.</summary>
    public const string StoragePrefix = "Storage:";

    /// <summary>S3 access key id.</summary>
    public const string S3AccessKeyId = "AccessKeyID";

    /// <summary>S3 secret access key.</summary>
    public const string S3SecretAccessKey = "SecretAccessKey";

    /// <summary>Azure connection string.</summary>
    public const string AzureConnectionString = "ConnectionString";

    /// <summary>SFTP/FTP password.</summary>
    public const string Password = "Password";

    /// <summary>SFTP private key content.</summary>
    public const string SftpPrivateKey = "PrivateKey";

    /// <summary>SFTP private key passphrase.</summary>
    public const string SftpPrivateKeyPassphrase = "PrivateKeyPassphrase";

    /// <summary>Master encryption key of the instance.</summary>
    public const string MasterEncryptionKey = StoragePrefix + "MasterEncryptionKey";

    /// <summary>Previous encryption key <paramref name="index"/> of the instance.</summary>
    public static string PreviousEncryptionKey(int index) => $"{StoragePrefix}PreviousEncryptionKeys:{index}";
}
