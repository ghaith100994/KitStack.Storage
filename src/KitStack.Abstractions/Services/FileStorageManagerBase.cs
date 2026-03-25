using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Utilities;
using Microsoft.AspNetCore.Http;

namespace KitStack.Abstractions.Services;

/// <summary>
/// Abstract base class for file storage managers.
/// Centralises the shared variant-processing pipeline so each provider only needs to implement
/// <see cref="CreateAsync{T}(IFormFile,string?,CancellationToken)"/> and <see cref="StoreVariantAsync"/>.
/// </summary>
public abstract class FileStorageManagerBase : IFileStorageManager
{
    /// <summary>Provider identifier written into every <see cref="FileEntry.StorageProvider"/>.</summary>
    protected abstract string StorageProvider { get; }

    /// <summary>
    /// Returns the active image-processing configuration, or <c>null</c> to skip variant generation.
    /// </summary>
    protected abstract ImageProcessingOptions? GetImageProcessingOptions();

    // ── IFileStorageManager ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public abstract Task<IFileEntry> CreateAsync<T>(
        IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class;

    /// <inheritdoc/>
    public async Task<IFileEntry> CreateAsync<T>(
        T entity, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class, IFileAttachable
    {
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);
        entity.AddFileAttachment(primary);
        primary.LinkToEntity(entity, category ?? typeof(T).Name);
        return primary;
    }

    /// <inheritdoc/>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(
        IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);
        var variants = new List<IFileEntry>();

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var options = GetImageProcessingOptions();

        if (!ImageProcessingHelper.IsImageExtension(extension) || options is null)
            return (primary, variants);

        var bytes = await ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        variants = await ProcessVariantsAsync(primary, bytes, category, options, cancellationToken)
            .ConfigureAwait(false);

        return (primary, variants);
    }

    // ── Shared variant pipeline ─────────────────────────────────────────────

    private async Task<List<IFileEntry>> ProcessVariantsAsync(
        IFileEntry primary,
        byte[] imageBytes,
        string? category,
        ImageProcessingOptions options,
        CancellationToken ct)
    {
        var variants = new List<IFileEntry>();
        var relativeFolder = Path.GetDirectoryName(primary.FileLocation)?.Replace('\\', '/') ?? string.Empty;

        if (options.CreateCompressed)
        {
            var (location, size) = await ResizeAndStoreAsync(
                imageBytes, relativeFolder, "compressed", $"{primary.Id:N}.jpg",
                options.CompressedMaxWidth, options.CompressedMaxHeight, options.JpegQuality,
                "compressed", ct).ConfigureAwait(false);

            var entry = BuildVariantEntry(location, size, "compressed", category, primary);
            entry.CopyRelationsFrom(primary);
            variants.Add(entry);
            (primary.Metadata ??= new Dictionary<string, string>())["CompressedPath"] = location;
        }

        if (options.CreateThumbnail)
        {
            var (location, size) = await ResizeAndStoreAsync(
                imageBytes, relativeFolder, "thumbnails", $"{primary.Id:N}.jpg",
                options.ThumbnailMaxWidth, options.ThumbnailMaxHeight, options.JpegQuality,
                "thumbnail", ct).ConfigureAwait(false);

            var entry = BuildVariantEntry(location, size, "thumbnail", category, primary);
            entry.CopyRelationsFrom(primary);
            variants.Add(entry);
            (primary.Metadata ??= new Dictionary<string, string>())["ThumbnailPath"] = location;
        }

        if (options.AdditionalSizes?.Count > 0)
        {
            var variantPaths = new List<string>();
            foreach (var size in options.AdditionalSizes)
            {
                if (string.IsNullOrWhiteSpace(size.SizeName)) continue;

                var (location, fileSize) = await ResizeAndStoreAsync(
                    imageBytes, relativeFolder, size.SizeName, $"{Guid.NewGuid():N}.jpg",
                    size.MaxWidth, size.MaxHeight, size.JpegQuality,
                    size.SizeName, ct).ConfigureAwait(false);

                var entry = BuildVariantEntry(location, fileSize, size.SizeName, category, primary);
                entry.CopyRelationsFrom(primary);
                variants.Add(entry);
                variantPaths.Add(location);
            }

            if (variantPaths.Count > 0)
                (primary.Metadata ??= new Dictionary<string, string>())["Variants"] = string.Join(';', variantPaths);
        }

        return variants;
    }

    private async Task<(string location, long size)> ResizeAndStoreAsync(
        byte[] imageBytes,
        string relativeFolder,
        string variantFolder,
        string fileName,
        int maxWidth, int maxHeight, int jpegQuality,
        string variantType,
        CancellationToken ct)
    {
        await using var outStream = new MemoryStream();
        using (var src = new MemoryStream(imageBytes))
            await ImageProcessingHelper.CreateResizedJpegToStreamAsync(
                src, outStream, maxWidth, maxHeight, jpegQuality, ct).ConfigureAwait(false);

        var size = outStream.Length;
        outStream.Seek(0, SeekOrigin.Begin);

        var location = await StoreVariantAsync(
            outStream, relativeFolder, variantFolder, fileName, variantType, ct).ConfigureAwait(false);

        return (location, size);
    }

    /// <summary>
    /// Stores the processed JPEG bytes and returns the provider-relative location string
    /// (relative path for Local/SFTP, S3 key for S3, etc.).
    /// </summary>
    /// <param name="data">Processed JPEG stream, position reset to 0 before this call.</param>
    /// <param name="relativeFolder">Provider-relative folder of the primary file, e.g. <c>category/Entity/Images</c>.</param>
    /// <param name="variantFolder">Sub-folder for this variant, e.g. <c>compressed</c>, <c>thumbnails</c>, or a custom size name.</param>
    /// <param name="fileName">Generated file name, e.g. <c>abcd1234ef.jpg</c>.</param>
    /// <param name="variantType">Logical variant type for target routing: <c>compressed</c>, <c>thumbnail</c>, or a custom size name.</param>
    protected abstract Task<string> StoreVariantAsync(
        MemoryStream data,
        string relativeFolder,
        string variantFolder,
        string fileName,
        string variantType,
        CancellationToken ct);

    /// <summary>
    /// Builds a <see cref="FileEntry"/> for a generated variant.
    /// Override to set additional provider-specific properties (e.g. <c>StoragePath</c> for Local).
    /// </summary>
    protected virtual FileEntry BuildVariantEntry(
        string location, long size, string variantType, string? category, IFileEntry primary)
    {
        return new FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(location),
            FileLocation = location,
            Size = size,
            ContentType = "image/jpeg",
            FileExtension = primary.FileExtension,
            UploadedTime = DateTime.UtcNow,
            VariantType = variantType,
            StorageProvider = StorageProvider,
            OriginalFileName = Path.GetFileName(location),
            LastAccessedTime = DateTimeOffset.UtcNow,
            Category = category,
            Encrypted = false,
            Metadata = new Dictionary<string, string>(),
        };
    }

    // ── Utilities ───────────────────────────────────────────────────────────

    /// <summary>Reads all bytes from <paramref name="file"/> into a new byte array.</summary>
    protected static async Task<byte[]> ReadAllBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream((int)file.Length);
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }
}
