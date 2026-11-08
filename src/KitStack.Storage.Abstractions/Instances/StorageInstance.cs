namespace KitStack.Storage.Abstractions.Instances;

/// <summary>
/// Storage instance names. Every <c>IFileStorageManager</c> serves one named instance; the single provider registered
/// by <c>AddLocal()</c>, <c>AddS3()</c>, ... without a name is <see cref="Default"/>.
/// </summary>
public static class StorageInstance
{
    /// <summary>Name of the instance registered without a name, and of entries whose <c>StorageName</c> is <c>null</c>.</summary>
    public const string Default = "default";

    /// <summary>Maximum length of an instance name (the database column size).</summary>
    public const int MaxNameLength = 100;

    /// <summary>Instance names compare case-insensitively.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Trimmed <paramref name="name"/>, or <see cref="Default"/> when it is null or blank.</summary>
    public static string Normalize(string? name) => string.IsNullOrWhiteSpace(name) ? Default : name.Trim();

    /// <summary>True when <paramref name="name"/> designates the default instance (null, blank or "default").</summary>
    public static bool IsDefault(string? name) => Comparer.Equals(Normalize(name), Default);

    /// <summary>True when both names designate the same instance.</summary>
    public static bool AreSame(string? a, string? b) => Comparer.Equals(Normalize(a), Normalize(b));

    /// <summary>Validate a name for registration.</summary>
    /// <exception cref="ArgumentException">The name is empty or longer than <see cref="MaxNameLength"/>.</exception>
    public static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
            throw new ArgumentException($"Storage instance names are limited to {MaxNameLength} characters.", nameof(name));
        return trimmed;
    }
}

/// <summary>What a provider type or a configured instance supports.</summary>
[Flags]
public enum StorageCapabilities
{
    /// <summary>Nothing beyond the core read/write/delete API.</summary>
    None = 0,

    /// <summary><c>GetUrlAsync</c> returns time-limited signed URLs (S3 presigned, Azure SAS).</summary>
    SignedUrls = 1,

    /// <summary><c>GetUrlAsync</c> returns plain public URLs (Local with <c>PublicBaseUrl</c>).</summary>
    PublicUrls = 2,

    /// <summary><c>OverwriteAsync</c> is supported.</summary>
    Overwrite = 4,

    /// <summary>Storage tiers can be chosen (Azure Hot/Cool/Cold/Archive).</summary>
    AccessTiers = 8,

    /// <summary>Provider-side encryption at rest (S3 SSE-S3 / SSE-KMS).</summary>
    ServerSideEncryption = 16,

    /// <summary>Image variants can be routed to their own target (S3 per-variant buckets).</summary>
    ImageVariantRouting = 32,
}
