using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using Microsoft.AspNetCore.Http;

namespace KitStack.Abstractions.Extensions;

/// <summary>
/// Convenience operations on <see cref="IFileStorageManager"/>.
/// </summary>
public static class FileStorageManagerExtensions
{
    /// <summary>
    /// Store a new file and then delete <paramref name="existing"/> (when not <c>null</c>), so updates do not
    /// leave the replaced file behind. The new file is stored first: if that fails, the existing file is untouched.
    /// If deleting the existing file fails, the exception propagates and the new file has not been
    /// linked anywhere yet, so the orphan sweeper removes it if the caller does not keep it.
    /// </summary>
    public static async Task<IFileEntry> ReplaceAsync<T>(
        this IFileStorageManager storage, IFileEntry? existing, Stream content, string fileName, string? contentType, string? category,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(storage);
        var created = await storage.CreateAsync<T>(content, fileName, contentType, category, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            await storage.DeleteAsync(existing, cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>Form-file overload of <see cref="ReplaceAsync{T}(IFileStorageManager,IFileEntry?,Stream,string,string?,string?,CancellationToken)"/>.</summary>
    public static async Task<IFileEntry> ReplaceAsync<T>(
        this IFileStorageManager storage, IFileEntry? existing, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(file);
        var created = await storage.CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            await storage.DeleteAsync(existing, cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>Open the file a <see cref="StoredFileRef"/> points at.</summary>
    public static Task<Stream> OpenReadAsync(this IFileStorageManager storage, StoredFileRef file, CancellationToken cancellationToken = default)
        => Require(storage).OpenReadAsync(ToEntry(file), cancellationToken);

    /// <summary>Check whether the file a <see cref="StoredFileRef"/> points at exists.</summary>
    public static Task<bool> ExistsAsync(this IFileStorageManager storage, StoredFileRef file, CancellationToken cancellationToken = default)
        => Require(storage).ExistsAsync(ToEntry(file), cancellationToken);

    /// <summary>Delete the file a <see cref="StoredFileRef"/> points at (only the original; use the entry to also delete variants).</summary>
    public static Task DeleteAsync(this IFileStorageManager storage, StoredFileRef file, CancellationToken cancellationToken = default)
        => Require(storage).DeleteAsync(ToEntry(file), cancellationToken);

    /// <summary>Get a direct URL for the file a <see cref="StoredFileRef"/> points at.</summary>
    public static Task<Uri?> GetUrlAsync(this IFileStorageManager storage, StoredFileRef file, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
        => Require(storage).GetUrlAsync(ToEntry(file), lifetime, cancellationToken);

    private static IFileStorageManager Require(IFileStorageManager storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage;
    }

    private static FileEntry ToEntry(StoredFileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.ToFileEntry();
    }
}
