using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Utilities;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Providers;
using Microsoft.AspNetCore.Http;
using System.Runtime.InteropServices;

namespace KitStack.Storage.Local.Services;

/// <summary>
/// Local file system implementation of IFileStorageManager.
/// Uses LocalOptions.Path as base directory.
/// </summary>
public class LocalFileStorageManager : IFileStorageManager
{
    private readonly LocalOptions _options;
    private readonly string _basePath;
    private readonly string _providerId;
    private readonly string _providerName;

    public LocalFileStorageManager(LocalStorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _options = provider.Options ?? throw new StorageConfigurationException("Local provider options are not configured.");
        _providerId = provider.Id;
        _providerName = provider.Name;
        _basePath = provider.EnsureBasePath();

        if (string.IsNullOrWhiteSpace(_basePath))
            throw new StorageConfigurationException("Storage path is not configured.");
    }

    /// <summary>
    /// Create and store the primary/original file for entity T. This stores only the original file.
    /// </summary>
    public async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(category))
            throw new StorageValidationException("Category is required.");
        if (string.IsNullOrWhiteSpace(_basePath))
            throw new StorageConfigurationException("Storage path is not configured.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var entityName = typeof(T).Name;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            entityName = entityName.Replace(@"\", "/", StringComparison.OrdinalIgnoreCase);

        var typeFolder = ImageProcessingHelper.GetFileTypeFolder(extension);
        var relativeFolderPath = Path.Combine(category, entityName, typeFolder);
        var uploadFolder = Path.Combine(_basePath, relativeFolderPath);
        Directory.CreateDirectory(uploadFolder);

        // Build FileEntry for original
        var fileEntry = new FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(file.FileName),
            OriginalFileName = file.FileName,
            Size = file.Length,
            ContentType = file.ContentType,
            UploadedTime = DateTime.UtcNow,
            FileExtension = extension,
            Category = category,
            Encrypted = false,
            StorageProvider = _providerName,
            ProviderId = _providerId,
            LastAccessedTime = DateTimeOffset.UtcNow,
            VariantType = "original",
        };

        fileEntry.Metadata ??= new Dictionary<string, string>();

        // Sanitize original file name
        var originalName = Path.GetFileNameWithoutExtension(file.FileName) ?? string.Empty;
        originalName = string.Concat(originalName.Split(Path.GetInvalidFileNameChars()));
        var fileName = $"{fileEntry.Id:N}-{originalName}{extension}";
        var fullFilePath = Path.Combine(uploadFolder, fileName);

        // Save original directly to disk (streamed)
        await using (var outFs = new FileStream(fullFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await file.CopyToAsync(outFs, cancellationToken).ConfigureAwait(false);
        }

        // Provider-relative path (URL-safe)
        fileEntry.FileLocation = Path.Combine(relativeFolderPath, fileName).Replace('\\', '/');

        return fileEntry;
    }


    public async Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class, IFileAttachable
    {
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);

        entity.AddFileAttachment(primary);
        primary.LinkToEntity(entity, category ?? typeof(T).Name);
        return primary;
    }

    /// <summary>
    /// Create and store the primary file and image variants as configured.
    /// Returns the primary FileEntry and a list of FileEntry objects for variants that were created.
    /// </summary>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        // First create the primary/original file (reuse logic)
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);

        var variants = new List<IFileEntry>();

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!ImageProcessingHelper.IsImageExtension(extension) || _options.ImageProcessing == null)
            return (primary, variants); // no image-processing required

        // Read bytes into memory once for variant creation
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();

        var relativeFolderPath = Path.GetDirectoryName(primary.FileLocation) ?? string.Empty;
        var uplaodFolderPath =  Path.Combine(_basePath, relativeFolderPath);
        // Compressed variant
        if (_options.ImageProcessing.CreateCompressed)
        {
            var compressedRelative = await CreateCompressedVariantAsync(bytes, uplaodFolderPath, relativeFolderPath, new Guid(primary.Id.ToString()), cancellationToken).ConfigureAwait(false);
            var compressedEntry = BuildVariantFileEntry(compressedRelative);
            compressedEntry.CopyRelationsFrom(primary);
            compressedEntry.Metadata ??= new Dictionary<string, string>();
            compressedEntry.VariantType = "compressed";
            compressedEntry.Category = category;
            compressedEntry.Encrypted = false;
            variants.Add(compressedEntry);

            // Also add reference in primary metadata
            primary.Metadata ??= new Dictionary<string, string>();
            primary.Metadata["CompressedPath"] = compressedRelative;
        }

        // Thumbnail variant
        if (_options.ImageProcessing.CreateThumbnail)
        {
            var thumbRelative = await CreateThumbnailVariantAsync(bytes, uplaodFolderPath, relativeFolderPath, new Guid(primary.Id.ToString()), cancellationToken).ConfigureAwait(false);
            var thumbEntry = BuildVariantFileEntry(thumbRelative);
            thumbEntry.CopyRelationsFrom(primary);
            thumbEntry.Metadata ??= new Dictionary<string, string>();
            thumbEntry.VariantType = "thumbnail";
            thumbEntry.Category = category;
            thumbEntry.Encrypted = false;

            variants.Add(thumbEntry);

            primary.Metadata ??= new Dictionary<string, string>();
            primary.Metadata["ThumbnailPath"] = thumbRelative;
        }

        // Additional sizes
        if (_options.ImageProcessing.AdditionalSizes != null && _options.ImageProcessing.AdditionalSizes.Count > 0)
        {
            var variantPaths = await CreateAdditionalVariantsAsync(bytes, uplaodFolderPath, relativeFolderPath, _options.ImageProcessing.AdditionalSizes, cancellationToken).ConfigureAwait(false);
            var variantEntries = variantPaths.Select(p =>
            {
                var ve = BuildVariantFileEntry(p);
                ve.CopyRelationsFrom(primary);
                ve.Metadata ??= new Dictionary<string, string>();
                ve.Category = category;
                ve.Encrypted = false;
                return ve;
            }).ToList();

            variants.AddRange(variantEntries);

            if (variantPaths.Count > 0)
            {
                primary.Metadata ??= new Dictionary<string, string>();
                primary.Metadata["Variants"] = string.Join(';', variantPaths);
            }
        }

        return (primary, variants);
    }

    private async Task<string> CreateCompressedVariantAsync(byte[] bytes, string uploadFolder, string relativeFolderPath, Guid fileId, CancellationToken cancellationToken)
    {
        var compressedFolder = Path.Combine(uploadFolder, "compressed");
        if (!Directory.Exists(compressedFolder))
            Directory.CreateDirectory(compressedFolder);

        var compressedFull = Path.Combine(compressedFolder, $"{fileId:N}.jpg");

        using var src = new MemoryStream(bytes);
        await using var outStream = new MemoryStream();
        await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream,
            _options.ImageProcessing.CompressedMaxWidth,
            _options.ImageProcessing.CompressedMaxHeight,
            _options.ImageProcessing.JpegQuality,
            cancellationToken).ConfigureAwait(false);

        outStream.Seek(0, SeekOrigin.Begin);
        await using (var fileOut = new FileStream(compressedFull, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await outStream.CopyToAsync(fileOut, cancellationToken).ConfigureAwait(false);
        }

        return Path.Combine(relativeFolderPath, "compressed", $"{fileId:N}.jpg").Replace('\\', '/');
    }

    private async Task<string> CreateThumbnailVariantAsync(byte[] bytes, string uploadFolder, string relativeFolderPath, Guid fileId, CancellationToken cancellationToken)
    {
        var thumbsFolder = Path.Combine(uploadFolder, "thumbnails");
        if (!Directory.Exists(thumbsFolder))
            Directory.CreateDirectory(thumbsFolder);

        var thumbFull = Path.Combine(thumbsFolder, $"{fileId:N}.jpg");

        using var src = new MemoryStream(bytes);
        await using var outStream = new MemoryStream();
        await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream,
            _options.ImageProcessing.ThumbnailMaxWidth,
            _options.ImageProcessing.ThumbnailMaxHeight,
            _options.ImageProcessing.JpegQuality,
            cancellationToken).ConfigureAwait(false);

        outStream.Seek(0, SeekOrigin.Begin);
        await using (var fileOut = new FileStream(thumbFull, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await outStream.CopyToAsync(fileOut, cancellationToken).ConfigureAwait(false);
        }

        return Path.Combine(relativeFolderPath, "thumbnails", $"{fileId:N}.jpg").Replace('\\', '/');
    }

    private async static Task<List<string>> CreateAdditionalVariantsAsync(byte[] bytes, string uploadFolder, string relativeFolderPath, IList<ImageSizeOption> sizes, CancellationToken cancellationToken)
    {
        var variants = new List<string>();

        foreach (var size in sizes)
        {
            if (string.IsNullOrWhiteSpace(size.SizeName)) continue;

            var sizeFolder = Path.Combine(uploadFolder, size.SizeName);
            if (!Directory.Exists(sizeFolder))
                Directory.CreateDirectory(sizeFolder);

            var sizeFileName = $"{Guid.NewGuid():N}.jpg";
            var sizeFullPath = Path.Combine(sizeFolder, sizeFileName);

            using var src = new MemoryStream(bytes);
            await using var outStream = new MemoryStream();
            await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream, size.MaxWidth, size.MaxHeight, size.JpegQuality, cancellationToken)
                .ConfigureAwait(false);

            outStream.Seek(0, SeekOrigin.Begin);
            await using (var fileOut = new FileStream(sizeFullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await outStream.CopyToAsync(fileOut, cancellationToken).ConfigureAwait(false);
            }

            variants.Add(Path.Combine(relativeFolderPath, size.SizeName, sizeFileName).Replace('\\', '/'));
        }

        return variants;
    }

    private FileEntry BuildVariantFileEntry(string relativePath)
    {
        // relativePath is provider-relative (e.g. "Module/Entity/Images/thumbnails/abcd.jpg")
        var full = GetFullPathFromRelative(relativePath);
        var info = new FileInfo(full);

        // Derive VariantType from the containing folder name (e.g. "thumbnails", "compressed", or custom size name)
        var dir = Path.GetDirectoryName(relativePath) ?? string.Empty;
        var variantType = Path.GetFileName(dir) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(variantType))
            variantType = null;

        var entry = new FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(relativePath),
            FileLocation = relativePath,
            Size = info.Exists ? info.Length : 0,
            ContentType = "image/jpeg",
            UploadedTime = DateTime.UtcNow,
            VariantType = variantType,
            StorageProvider = _providerName,
            ProviderId = _providerId,
            OriginalFileName = Path.GetFileName(relativePath),
            LastAccessedTime = DateTimeOffset.UtcNow,
        };

        return entry;
    }

    private string GetFullPathFromRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new StorageValidationException("Relative path must be provided.");

        // Normalize comparison strategy depending on OS
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var baseFull = Path.GetFullPath(_basePath);

        // If caller passed an absolute path, resolve and ensure it stays under base
        if (Path.IsPathRooted(relativePath))
        {
            var absolute = Path.GetFullPath(relativePath);
            if (!absolute.StartsWith(baseFull, comparison) && !string.Equals(absolute, baseFull, comparison))
                throw new StorageValidationException("File path escapes base storage path.");
            return absolute;
        }

        // Trim leading slashes so Path.Combine behaves as intended
        var relative = relativePath.TrimStart('/', '\\');

        var combined = Path.Combine(_basePath, relative);
        var full = Path.GetFullPath(combined);

        // Ensure the resolved path is inside the configured base path (prevent directory traversal)
        if (!full.StartsWith(baseFull, comparison) && !string.Equals(full, baseFull, comparison))
            throw new StorageValidationException("File path escapes base storage path.");

        return full;
    }
}
