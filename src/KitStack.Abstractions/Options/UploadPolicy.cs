using KitStack.Abstractions.Exceptions;

namespace KitStack.Abstractions.Options;

/// <summary>
/// Upload rules enforced by every storage manager before the provider is called.
/// Bound from <c>Storage:Upload</c>. A violation throws <see cref="StorageValidationException"/>.
/// </summary>
public sealed class UploadPolicy
{
    /// <summary>Maximum file size in bytes. 0 (default) means no limit.</summary>
    public long MaxBytes { get; set; }

    /// <summary>
    /// Allowed resolved content types, for example <c>image/png</c> or <c>image/*</c>.
    /// Empty (default) allows any type. Matching is case-insensitive.
    /// </summary>
    public ISet<string> AllowedContentTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allowed file extensions including the dot, for example <c>.pdf</c>.
    /// Empty (default) allows any extension. Matching is case-insensitive.
    /// </summary>
    public ISet<string> AllowedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="contentType"/> is permitted by <see cref="AllowedContentTypes"/>.</summary>
    public bool IsContentTypeAllowed(string contentType)
    {
        if (AllowedContentTypes.Count == 0)
            return true;

        foreach (var allowed in AllowedContentTypes)
        {
            if (string.Equals(allowed, contentType, StringComparison.OrdinalIgnoreCase))
                return true;
            if (allowed.EndsWith("/*", StringComparison.Ordinal)
                && contentType.StartsWith(allowed[..^1], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="extension"/> is permitted by <see cref="AllowedExtensions"/>.</summary>
    public bool IsExtensionAllowed(string extension)
    {
        if (AllowedExtensions.Count == 0)
            return true;
        if (string.IsNullOrEmpty(extension))
            return false;
        return AllowedExtensions.Contains(extension.StartsWith('.') ? extension : "." + extension);
    }
}
