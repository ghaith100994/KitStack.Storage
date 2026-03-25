using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Services;
using KitStack.Abstractions.Utilities;
using KitStack.Storage.Local.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

namespace KitStack.Storage.Local.Services;

/// <summary>
/// Local file system implementation of <see cref="IFileStorageManager"/>.
/// Uses <see cref="LocalOptions.Path"/> as the base directory.
/// </summary>
public class LocalFileStorageManager : FileStorageManagerBase
{
    private readonly LocalOptions _option;
    private readonly string _basePath;

    protected override string StorageProvider => "Local";

    public LocalFileStorageManager(IOptions<LocalOptions> option)
    {
        _option = option?.Value ?? throw new ArgumentNullException(nameof(option));

        if (string.IsNullOrWhiteSpace(_option.Path))
            _option.Path = Path.Combine(Directory.GetCurrentDirectory(), "Files");

        _basePath = Path.IsPathRooted(_option.Path) ? _option.Path : Path.Combine(Directory.GetCurrentDirectory(), _option.Path);

        if (_option.EnsureBasePathExists && !Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _option.ImageProcessing;

    /// <summary>
    /// Stores the primary/original file on disk and returns its populated <see cref="IFileEntry"/>.
    /// </summary>
    public override async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(category))
            throw new StorageValidationException("Category is required.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var entityName = typeof(T).Name;

        var typeFolder = ImageProcessingHelper.GetFileTypeFolder(extension);
        var relativeFolderPath = Path.Combine(category, entityName, typeFolder);
        var uploadFolder = Path.Combine(_basePath, relativeFolderPath);
        Directory.CreateDirectory(uploadFolder);

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
            StorageProvider = StorageProvider,
            LastAccessedTime = DateTimeOffset.UtcNow,
            VariantType = "original",
            Metadata = new Dictionary<string, string>(),
        };

        var originalName = string.Concat(
            (Path.GetFileNameWithoutExtension(file.FileName) ?? string.Empty)
            .Split(Path.GetInvalidFileNameChars()));
        var fileName = $"{fileEntry.Id:N}-{originalName}{extension}";
        var fullFilePath = Path.Combine(uploadFolder, fileName);

        await using (var outFs = new FileStream(fullFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await file.CopyToAsync(outFs, cancellationToken).ConfigureAwait(false);

        // Provider-relative path (URL-safe, no base dir prefix)
        fileEntry.FileLocation = Path.Combine(relativeFolderPath, fileName).Replace('\\', '/');
        // Full storage path including the configured base directory (URL-safe)
        fileEntry.StoragePath = Path.Combine(_option.Path, relativeFolderPath, fileName).Replace('\\', '/');

        return fileEntry;
    }


    /// <summary>
    /// Writes the processed JPEG to disk under <c>relativeFolder/variantFolder/</c> and returns
    /// the provider-relative path (no base directory prefix).
    /// </summary>
    protected override async Task<string> StoreVariantAsync(
        MemoryStream data,
        string relativeFolder,
        string variantFolder,
        string fileName,
        string variantType,
        CancellationToken ct)
    {
        var uploadFolder = Path.Combine(_basePath, relativeFolder, variantFolder);
        Directory.CreateDirectory(uploadFolder);

        var fullPath = Path.Combine(uploadFolder, fileName);
        await using (var fileOut = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await data.CopyToAsync(fileOut, ct).ConfigureAwait(false);

        return Path.Combine(relativeFolder, variantFolder, fileName).Replace('\\', '/');
    }

    /// <summary>
    /// Extends the base variant entry with <see cref="IFileEntry.StoragePath"/>.
    /// </summary>
    protected override FileEntry BuildVariantEntry(
        string location, long size, string variantType, string? category, IFileEntry primary)
    {
        var entry = base.BuildVariantEntry(location, size, variantType, category, primary);
        entry.StoragePath = Path.Combine(_option.Path, location).Replace('\\', '/');
        return entry;
    }


    /// <summary>
    /// Resolves a provider-relative or absolute path to its full disk path,
    /// guarding against directory-traversal attacks.
    /// </summary>
    protected string GetFullPathFromRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new StorageValidationException("Relative path must be provided.");

        // Normalize comparison strategy depending on OS
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var baseFull = Path.GetFullPath(_basePath);

        if (Path.IsPathRooted(relativePath))
        {
            var absolute = Path.GetFullPath(relativePath);
            if (!absolute.StartsWith(baseFull, comparison) && !string.Equals(absolute, baseFull, comparison))
                throw new StorageValidationException("File path escapes base storage path.");
            return absolute;
        }

        var combined = Path.Combine(_basePath, relativePath.TrimStart('/', '\\'));
        var full = Path.GetFullPath(combined);

        if (!full.StartsWith(baseFull, comparison) && !string.Equals(full, baseFull, comparison))
            throw new StorageValidationException("File path escapes base storage path.");

        return full;
    }
}
