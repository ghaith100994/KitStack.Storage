using System.Runtime.InteropServices;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using KitStack.Storage.Local.Options;
using Microsoft.Extensions.Options;

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

    public LocalFileStorageManager(
        IOptions<LocalOptions> option,
        IOptions<StorageOptions>? storageOptions = null,
        IFileContentEncryptor? encryptor = null)
        : base(storageOptions?.Value, encryptor)
    {
        _option = option?.Value ?? throw new ArgumentNullException(nameof(option));

        if (string.IsNullOrWhiteSpace(_option.Path))
            _option.Path = Path.Combine(Directory.GetCurrentDirectory(), "Files");

        _basePath = Path.GetFullPath(Path.IsPathRooted(_option.Path) ? _option.Path : Path.Combine(Directory.GetCurrentDirectory(), _option.Path));

        if (_option.EnsureBasePathExists && !Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _option.ImageProcessing;

    /// <summary>Writes the content under the base directory and returns the provider-relative path.</summary>
    protected override async Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var relativePath = $"{request.Folder}/{request.FileName}";
        var fullPath = GetFullPathFromRelative(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using (var outFs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await request.Content.CopyToAsync(outFs, cancellationToken).ConfigureAwait(false);

        return relativePath;
    }

    protected override Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        var fullPath = GetFullPathFromRelative(location.Location);
        try
        {
            Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    protected override Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => Task.FromResult(File.Exists(GetFullPathFromRelative(location.Location)));

    protected override Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        // File.Delete does not throw when the file is missing (only when its directory is).
        var fullPath = GetFullPathFromRelative(location.Location);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    protected override Task<Uri?> GetUrlCoreAsync(StorageLocation location, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_option.PublicBaseUrl))
            return Task.FromResult<Uri?>(null);

        var escaped = string.Join('/', location.Location.Split('/').Select(Uri.EscapeDataString));
        return Task.FromResult<Uri?>(new Uri($"{_option.PublicBaseUrl.TrimEnd('/')}/{escaped}"));
    }

    /// <summary>Full storage path including the configured base directory (URL-safe).</summary>
    protected override string? GetStoragePath(string location)
        => Path.Combine(_option.Path, location).Replace('\\', '/');

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

        var baseWithSeparator = _basePath.EndsWith(Path.DirectorySeparatorChar) ? _basePath : _basePath + Path.DirectorySeparatorChar;
        var full = Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(_basePath, relativePath.TrimStart('/', '\\')));

        // A plain StartsWith(base) would accept "/data/Files-other/..." for base "/data/Files".
        if (!full.StartsWith(baseWithSeparator, comparison))
            throw new StorageValidationException("File path escapes base storage path.");

        return full;
    }
}
