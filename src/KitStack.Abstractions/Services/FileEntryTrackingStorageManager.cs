using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using Microsoft.AspNetCore.Http;

namespace KitStack.Abstractions.Services;

/// <summary>
/// Decorator that records created files in an <see cref="IFileEntryStore"/> and marks them deleted when
/// they are deleted. Registered by <c>AddFileEntryTracking()</c>.
/// </summary>
public sealed class FileEntryTrackingStorageManager(IFileStorageManager inner, IFileEntryStore store) : IFileStorageManager
{
    /// <inheritdoc/>
    public async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
        => await TrackAsync(await inner.CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IFileEntry> CreateAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default)
        where T : class
        => await TrackAsync(await inner.CreateAsync<T>(content, fileName, contentType, category, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class, IFileAttachable
        => await TrackAsync(await inner.CreateAsync(entity, file, category, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
        => await TrackAsync(await inner.CreateWithVariantsAsync<T>(file, category, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default)
        where T : class
        => await TrackAsync(await inner.CreateWithVariantsAsync<T>(content, fileName, contentType, category, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(IFileEntry entry, CancellationToken cancellationToken = default)
        => inner.OpenReadAsync(entry, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(IFileEntry entry, CancellationToken cancellationToken = default)
        => inner.ExistsAsync(entry, cancellationToken);

    /// <inheritdoc/>
    public async Task DeleteAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await inner.DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
        await store.MarkDeletedAsync(entry.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<Uri?> GetUrlAsync(IFileEntry entry, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
        => inner.GetUrlAsync(entry, lifetime, cancellationToken);

    private async Task<IFileEntry> TrackAsync(IFileEntry entry, CancellationToken cancellationToken)
    {
        await TrackAsync((entry, []), cancellationToken).ConfigureAwait(false);
        return entry;
    }

    private async Task<(IFileEntry Primary, List<IFileEntry> Variants)> TrackAsync(
        (IFileEntry Primary, List<IFileEntry> Variants) created, CancellationToken cancellationToken)
    {
        try
        {
            await store.AddAsync(created.Primary, cancellationToken).ConfigureAwait(false);
            foreach (var variant in created.Variants)
                await store.AddAsync(variant, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Without a metadata record nobody can find the bytes again: remove them rather than leak them.
            await inner.DeleteAsync(created.Primary, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return created;
    }
}
