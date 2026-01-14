using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Sftp.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using FluentFTP;
using KitStack.Abstractions.Utilities;

namespace KitStack.Storage.Sftp.Services;

public class SftpFileStorageManager : IFileStorageManager, IDisposable
{
    private readonly SftpOptions _options;
    private readonly FtpClient _client;
    private bool _disposed;

    public SftpFileStorageManager(IOptions<SftpOptions> options)
    {
        _options = options.Value;

        // Default remote path similar to local provider
        if (string.IsNullOrWhiteSpace(_options.RemotePath))
            _options.RemotePath = "Files";

        // Build FluentFTP client (use basic constructor and set port)
        var client = new FtpClient(_options.Host, _options.Username ?? string.Empty, _options.Password ?? string.Empty);
        client.Port = _options.Port;

        // Try to connect now and optionally ensure base path exists
        try
        {
            client.Connect();
            if (_options.EnsureRemotePathExists && !string.IsNullOrWhiteSpace(_options.RemotePath))
            {
                var baseDir = _options.RemotePath.Trim('/');
                if (!string.IsNullOrWhiteSpace(baseDir))
                    client.CreateDirectory(baseDir);
            }
        }
        catch
        {
            // Swallow connect errors here; operations will attempt to connect later.
        }

        _client = client;
    }

    public async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category is required.", nameof(category));

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var entityName = typeof(T).Name;

        var typeFolder = ImageProcessingHelper.GetFileTypeFolder(extension);
        var remoteFolder = CombineRemotePath(_options.RemotePath, category, entityName, typeFolder);

        var id = Guid.NewGuid();
        var originalName = Path.GetFileNameWithoutExtension(file.FileName) ?? string.Empty;
        originalName = string.Concat(originalName.Split(Path.GetInvalidFileNameChars()));
        var fileName = $"{id:N}-{originalName}{extension}";
        var remoteFilePath = CombineRemotePath(remoteFolder, fileName);

        EnsureConnected();

        var remotePath = remoteFilePath.TrimStart('/');
        var remoteDir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/') ?? string.Empty;
        if (_options.EnsureRemotePathExists && !string.IsNullOrWhiteSpace(remoteDir))
        {
            _client.CreateDirectory(remoteDir);
        }

        // FluentFTP upload: write to a temp file then upload (avoids API surface differences across versions)
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using (var outFs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await file.OpenReadStream().CopyToAsync(outFs, cancellationToken).ConfigureAwait(false);
            }

            _client.UploadFile(tempFile, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: false);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }

        var entry = new FileEntry
        {
            Id = id,
            FileName = Path.GetFileName(file.FileName),
            FileLocation = remoteFilePath.TrimStart('/'),
            Size = file.Length,
            ContentType = file.ContentType,
            UploadedTime = DateTimeOffset.UtcNow,
            Category= category,
            Encrypted = false,
            FileExtension = extension,
            VariantType = "original",
        };

        return entry;
    }

    public async Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class, IFileAttachable
    {
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);
        entity.AddFileAttachment(primary);
        return primary;
    }

    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class
    {
        // First create the primary/original file (reuse logic)
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);

        var variants = new List<IFileEntry>();

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var ip = _options.ImageProcessing;
        if (!ImageProcessingHelper.IsImageExtension(extension) || ip == null)
            return (primary, variants); // no image-processing required

        // Read bytes into memory once for variant creation
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();

        // remote relative paths are provider relative, reuse primary.FileLocation
        var relativeFolderPath = Path.GetDirectoryName(primary.FileLocation) ?? string.Empty;
        var uploadFolder = Path.Combine(Path.GetTempPath(), "kitstack-ftp-variants", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uploadFolder);

        var fileName = $"{primary.Id:N}-{primary.FileName}{primary.FileExtension}";

        try
        {
            if (ip.CreateCompressed)
            {
                var (compressedRelative, compressedSize) = await CreateCompressedVariantAsync(bytes, uploadFolder, relativeFolderPath, fileName, ip, cancellationToken).ConfigureAwait(false);
                var compressedEntry = BuildVariantFileEntry(compressedRelative, compressedSize, "compressed", primary.FileExtension);
                compressedEntry.Metadata ??= new Dictionary<string, string>();
                compressedEntry.Category = category;
                compressedEntry.Encrypted = false;
                variants.Add(compressedEntry);

                primary.Metadata ??= new Dictionary<string, string>();
                primary.Metadata["CompressedPath"] = compressedRelative;
            }

            if (ip.CreateThumbnail)
            {
                var (thumbRelative, thumbSize) = await CreateThumbnailVariantAsync(bytes, uploadFolder, relativeFolderPath, fileName, ip, cancellationToken).ConfigureAwait(false);
                var thumbEntry = BuildVariantFileEntry(thumbRelative, thumbSize, "thumbnail", primary.FileExtension);
                thumbEntry.Metadata ??= new Dictionary<string, string>();
                thumbEntry.Category = category;
                thumbEntry.Encrypted = false;

                variants.Add(thumbEntry);

                primary.Metadata ??= new Dictionary<string, string>();
                primary.Metadata["ThumbnailPath"] = thumbRelative;
            }

            // Additional sizes
            if (ip.AdditionalSizes != null && ip.AdditionalSizes.Count > 0)
            {
                var additionalPaths = new List<string>();
                foreach (var size in ip.AdditionalSizes)
                {
                    if (string.IsNullOrWhiteSpace(size.SizeName)) continue;
                    var (variantPath, variantSize) = await CreateAdditionalVariantAsync(bytes, uploadFolder, relativeFolderPath, size, cancellationToken).ConfigureAwait(false);
                    var ve = BuildVariantFileEntry(variantPath, variantSize, size.SizeName, primary.FileExtension);
                    ve.Metadata ??= new Dictionary<string, string>();
                    ve.Category = category;
                    ve.Encrypted = false;
                    variants.Add(ve);
                    additionalPaths.Add(variantPath);
                }

                if (additionalPaths.Count > 0)
                {
                    primary.Metadata ??= new Dictionary<string, string>();
                    primary.Metadata["Variants"] = string.Join(';', additionalPaths);
                }
            }

            return (primary, variants);
        }
        finally
        {
            try { if (Directory.Exists(uploadFolder)) Directory.Delete(uploadFolder, true); } catch { }
        }
    }

    private static FileEntry BuildVariantFileEntry(string relativePath, long size, string? variantType, string? fileExtension)
    {
        var entry = new FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(relativePath),
            FileLocation = relativePath,
            Size = size,
            ContentType = "image/jpeg",
            FileExtension = fileExtension,
            UploadedTime = DateTimeOffset.UtcNow,
            VariantType = variantType,
        };

        return entry;
    }

    private async Task<(string RelativePath, long Size)> CreateCompressedVariantAsync(byte[] bytes, string uploadFolder, string relativeFolderPath, string fileName, ImageProcessingOptions options, CancellationToken cancellationToken)
    {
        var compressedFolder = Path.Combine(uploadFolder, "compressed");
        if (!Directory.Exists(compressedFolder))
            Directory.CreateDirectory(compressedFolder);

        var compressedFull = Path.Combine(compressedFolder, fileName);

        using var src = new MemoryStream(bytes);
        await using var outStream = new MemoryStream();
        await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream,
            options.CompressedMaxWidth,
            options.CompressedMaxHeight,
            options.JpegQuality,
            cancellationToken).ConfigureAwait(false);

        var variantSize = outStream.Length;
        outStream.Seek(0, SeekOrigin.Begin);
        await using (var fileOut = new FileStream(compressedFull, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await outStream.CopyToAsync(fileOut, cancellationToken).ConfigureAwait(false);
        }

        // Upload compressed to remote via FTP
        var remoteRelative = Path.Combine(relativeFolderPath, "compressed", fileName).Replace('\\', '/');
        EnsureConnected();
        _client.UploadFile(compressedFull, remoteRelative, FtpRemoteExists.Overwrite, createRemoteDir: true);
        return (remoteRelative, variantSize);
    }

    private async Task<(string RelativePath, long Size)> CreateThumbnailVariantAsync(byte[] bytes, string uploadFolder, string relativeFolderPath, string fileName, ImageProcessingOptions options, CancellationToken cancellationToken)
    {
        var thumbsFolder = Path.Combine(uploadFolder, "thumbnails");
        if (!Directory.Exists(thumbsFolder))
            Directory.CreateDirectory(thumbsFolder);

        var thumbFull = Path.Combine(thumbsFolder, fileName);

        using var src = new MemoryStream(bytes);
        await using var outStream = new MemoryStream();
        await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream,
            options.ThumbnailMaxWidth,
            options.ThumbnailMaxHeight,
            options.JpegQuality,
            cancellationToken).ConfigureAwait(false);

        var variantSize = outStream.Length;
        outStream.Seek(0, SeekOrigin.Begin);
        await using (var fileOut = new FileStream(thumbFull, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await outStream.CopyToAsync(fileOut, cancellationToken).ConfigureAwait(false);
        }

        var remoteRelative = Path.Combine(relativeFolderPath, "thumbnails", fileName).Replace('\\', '/');
        EnsureConnected();
        _client.UploadFile(thumbFull, remoteRelative, FtpRemoteExists.Overwrite, createRemoteDir: true);
        return (remoteRelative, variantSize);
    }

    private async Task<(string RelativePath, long Size)> CreateAdditionalVariantAsync(byte[] bytes, string uploadFolder, string relativeFolderPath, ImageSizeOption size, CancellationToken cancellationToken)
    {
        var sizeFolder = Path.Combine(uploadFolder, size.SizeName);
        if (!Directory.Exists(sizeFolder))
            Directory.CreateDirectory(sizeFolder);

        var sizeFileName = $"{Guid.NewGuid():N}.jpg";
        var sizeFullPath = Path.Combine(sizeFolder, sizeFileName);

        using var src = new MemoryStream(bytes);
        await using var outStream = new MemoryStream();
        await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream, size.MaxWidth, size.MaxHeight, size.JpegQuality, cancellationToken)
            .ConfigureAwait(false);

        var variantSize = outStream.Length;
        outStream.Seek(0, SeekOrigin.Begin);
        await using (var fileOut = new FileStream(sizeFullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await outStream.CopyToAsync(fileOut, cancellationToken).ConfigureAwait(false);
        }

        var remoteRelative = Path.Combine(relativeFolderPath, size.SizeName, sizeFileName).Replace('\\', '/');
        EnsureConnected();
        _client.UploadFile(sizeFullPath, remoteRelative, FtpRemoteExists.Overwrite, createRemoteDir: true);
        return (remoteRelative, variantSize);
    }

    private static string CombineRemotePath(params string?[] parts)
    {
        var clean = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim('/'));
        var joined = string.Join('/', clean);
        return "/" + joined;
    }

    private void EnsureConnected()
    {
        if (_client.IsConnected)
            return;

        _client.Connect();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try { _client.Dispose(); } catch { }
            }
            _disposed = true;
        }
    }
}
