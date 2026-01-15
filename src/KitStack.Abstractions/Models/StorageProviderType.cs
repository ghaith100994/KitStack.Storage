using KitStack.Abstractions.Extensions;

namespace KitStack.Abstractions.Models;

/// <summary>
/// Light-weight string-backed provider type helper.
/// Use StorageProviderType.Local / .S3 etc. in code, or pass custom strings when needed.
/// Instances compare case-insensitively and can be implicitly converted from/to string.
/// </summary>
public sealed class StorageProviderType
{
    public string Value { get; }

    private StorageProviderType(string value)
    {
        if (value.IsEmpty()) 
            throw new ArgumentException("value is required", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;

    public override bool Equals(object? obj)
    {
        return obj is StorageProviderType other
            ? string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase)
            : obj is string s && string.Equals(Value, s, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public static implicit operator string(StorageProviderType t) => t.Value;

    /// <summary>
    /// Creates a <see cref="StorageProviderType"/> from a string value.
    /// </summary>
    /// <param name="value">The string value representing the provider type.</param>
    /// <returns>A new <see cref="StorageProviderType"/> instance.</returns>
    public static StorageProviderType FromString(string value) => new(value);

    // Common constants
    public static readonly StorageProviderType Local = new("Local");
    public static readonly StorageProviderType S3 = new("S3");
    public static readonly StorageProviderType Sftp = new("Sftp");
    public static readonly StorageProviderType AzureBlob = new("AzureBlob");
    public static readonly StorageProviderType InMemory = new("InMemory");
    public static readonly StorageProviderType Fake = new("Fake");
}
