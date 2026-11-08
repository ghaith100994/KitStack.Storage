using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using Microsoft.AspNetCore.Http;

namespace KitStack.Storage.Abstractions.Services;

/// <summary>
/// Decorator that records created files in an <see cref="IFileEntryStore"/> and marks them deleted when
/// they are deleted. Registered by <c>AddFileEntryTracking()</c>; managers returned by
/// <see cref="IFileStorageManagerProvider"/> are wrapped in it too when tracking is enabled.
/// </summary>
/// <remarks>
/// With a <paramref name="provider"/>, operations on an entry whose <see cref="IFileEntry.StorageName"/> names another
/// instance are routed to that instance. Entries without a <c>StorageName</c> are handled by <paramref name="inner"/>.
/// </remarks>
public sealed class FileEntryTrackingStorageManager(IFileStorageManager inner, IFileEntryStore store, IFileStorageManagerProvider? provider)
    : IFileStorageManager, IStorageContentAccess, IFileStorageManagerDecorator
{
    /// <summary>Create the decorator without instance routing (0.2 behaviour).</summary>
    public FileEntryTrackingStorageManager(IFileStorageManager inner, IFileEntryStore store)
        : this(inner, store, provider: null)
    {
    }

    /// <inheritdoc/>
    public IFileStorageManager Inner => inner;

    /// <inheritdoc/>
    public string StorageName => inner.StorageName;

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
    public async Task<IFileEntry> CreateAsync(StorageWriteOptions options, Stream content, CancellationToken cancellationToken = default)
        => await TrackAsync(await inner.CreateAsync(options, content, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync(StorageWriteOptions options, Stream content, CancellationToken cancellationToken = default)
        => await TrackAsync(await inner.CreateWithVariantsAsync(options, content, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(IFileEntry entry, CancellationToken cancellationToken = default)
        => (Route(entry) ?? inner).OpenReadAsync(entry, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(IFileEntry entry, CancellationToken cancellationToken = default)
        => (Route(entry) ?? inner).ExistsAsync(entry, cancellationToken);

    /// <inheritdoc/>
    public async Task DeleteAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Route(entry) is { } owner)
        {
            await owner.DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        await inner.DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
        await store.MarkDeletedAsync(entry.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task OverwriteAsync(IFileEntry entry, Stream content, CancellationToken cancellationToken = default)
    {
        if (Route(entry) is { } owner)
        {
            await owner.OverwriteAsync(entry, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        await inner.OverwriteAsync(entry, content, cancellationToken).ConfigureAwait(false);
        await store.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<Uri?> GetUrlAsync(IFileEntry entry, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
        => (Route(entry) ?? inner).GetUrlAsync(entry, lifetime, cancellationToken);

    /// <inheritdoc/>
    public string? PredictLocation(string folder, string fileName, string variantType)
        => (inner as IStorageContentAccess)?.PredictLocation(folder, fileName, variantType);

    /// <inheritdoc/>
    public Task<StoredContent> PutContentAsync(StorageContentWrite request, Stream plaintext, CancellationToken cancellationToken = default)
        => ContentAccess.PutContentAsync(request, plaintext, cancellationToken);

    /// <inheritdoc/>
    public Task<StoredContent?> VerifyContentAsync(string location, string variantType, CancellationToken cancellationToken = default)
        => ContentAccess.VerifyContentAsync(location, variantType, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteContentAsync(string location, string variantType, CancellationToken cancellationToken = default)
        => ContentAccess.DeleteContentAsync(location, variantType, cancellationToken);

    private IStorageContentAccess ContentAccess
        => inner as IStorageContentAccess ?? throw new NotSupportedException($"{inner.GetType().Name} does not support direct content access.");

    /// <summary>The manager of another instance when <paramref name="entry"/> explicitly belongs to one; otherwise <c>null</c>.</summary>
    private IFileStorageManager? Route(IFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (provider is null || string.IsNullOrWhiteSpace(entry.StorageName) || StorageInstance.AreSame(entry.StorageName, inner.StorageName))
            return null;
        return provider.Get(entry);
    }

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
