using System.Text;
using KitStack.Abstractions.Exceptions;

namespace KitStack.Abstractions.Utilities;

/// <summary>
/// Builds provider-relative storage paths from untrusted input (categories, entity names, uploaded file names).
/// Output only contains letters, digits, '-', '_' and '.', joined with '/', so it is safe as a disk path,
/// an S3 or blob key, or an (S)FTP path. Unicode letters are kept.
/// </summary>
public static class StoragePaths
{
    private const int MaxSegmentLength = 100;

    /// <summary>Join non-empty segments with '/'.</summary>
    public static string Combine(params string?[] segments)
        => string.Join('/', segments.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim('/')));

    /// <summary>
    /// Sanitize a category. '/' or '\' separate nested folders; '.' and '..' segments are rejected.
    /// </summary>
    /// <exception cref="StorageValidationException">The category is empty or contains a relative segment.</exception>
    public static string SanitizeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new StorageValidationException("Category is required.");

        var segments = category.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(s => s is "." or ".."))
            throw new StorageValidationException("Category must not contain relative path segments.");

        var sanitized = Combine(segments.Select(SanitizeSegment).ToArray());
        if (sanitized.Length == 0)
            throw new StorageValidationException("Category is required.");
        return sanitized;
    }

    /// <summary>Sanitize a single path segment (no separators). Returns "file" when nothing usable is left.</summary>
    public static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "file";

        var builder = new StringBuilder(Math.Min(value.Length, MaxSegmentLength));
        var lastWasDash = false;
        foreach (var c in value.Trim())
        {
            if (builder.Length >= MaxSegmentLength)
                break;

            if (char.IsLetterOrDigit(c) || c is '_' or '.')
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var result = builder.ToString().Trim('-', '.');
        return result.Length == 0 ? "file" : result;
    }

    /// <summary>
    /// Lower-case extension of <paramref name="fileName"/> including the dot, restricted to ASCII letters
    /// and digits (max 10 characters). Returns an empty string when there is no usable extension.
    /// </summary>
    public static string GetExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (ext.Length < 2 || ext.Length > 11)
            return string.Empty;
        return ext.Skip(1).All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) ? ext : string.Empty;
    }
}
