using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Services;
using KitStack.Abstractions.Utilities;
using KitStack.Storage.Sftp.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using FluentFTP;

namespace KitStack.Storage.Sftp.Services;

public class SftpFileStorageManager : FileStorageManagerBase, IDisposable
{
    private readonly SftpOptions _options;
    private readonly FtpClient _client;
    private bool _disposed;

    protected override string StorageProvider => "Sftp";

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

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _options.ImageProcessing;

    public override async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(category))
            throw new StorageValidationException("Category is required.");

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
            OriginalFileName = file.FileName,
            FileLocation = remoteFilePath.TrimStart('/'),
            Size = file.Length,
            ContentType = file.ContentType,
            UploadedTime = DateTimeOffset.UtcNow,
            Category = category,
            Encrypted = false,
            FileExtension = extension,
            VariantType = "original",
            StorageProvider = StorageProvider,
            LastAccessedTime = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(),
        };

        return entry;
    }

    /// <summary>
    /// Writes the processed JPEG to a temp file, uploads it via FTP, then returns the remote path.
    /// </summary>
    protected override async Task<string> StoreVariantAsync(
        MemoryStream data,
        string relativeFolder,
        string variantFolder,
        string fileName,
        string variantType,
        CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using (var fileOut = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await data.CopyToAsync(fileOut, ct).ConfigureAwait(false);

            var remotePath = $"{relativeFolder}/{variantFolder}/{fileName}".Replace('\\', '/').TrimStart('/');
            EnsureConnected();
            _client.UploadFile(tempFile, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
            return remotePath;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
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
