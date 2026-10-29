using FluentFTP;
using KitStack.Abstractions.IO;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using KitStack.Abstractions.Utilities;
using KitStack.Storage.Ftp.Options;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Ftp.Services;

/// <summary>
/// FTP/FTPS implementation of <c>IFileStorageManager</c> built on FluentFTP.
/// One connection is shared and operations are serialized on it.
/// </summary>
public sealed class FtpFileStorageManager : FileStorageManagerBase, IAsyncDisposable, IDisposable
{
    private readonly FtpOptions _options;
    private readonly AsyncFtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    protected override string StorageProvider => "Ftp";

    public FtpFileStorageManager(
        IOptions<FtpOptions> options,
        IOptions<StorageOptions>? storageOptions = null,
        IFileContentEncryptor? encryptor = null)
        : base(storageOptions?.Value, encryptor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.RemotePath))
            _options.RemotePath = "Files";
        _client = FtpClientFactory.Create(_options);
    }

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _options.ImageProcessing;

    protected override Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var remotePath = StoragePaths.Combine(_options.RemotePath, request.Folder, request.FileName);
        return UploadAsync(remotePath, request.Content, cancellationToken);
    }

    protected override Task OverwriteCoreAsync(StorageLocation location, Stream content, long length, string contentType, CancellationToken cancellationToken)
        => UploadAsync(location.Location, content, cancellationToken);

    private Task<string> UploadAsync(string remotePath, Stream content, CancellationToken cancellationToken)
        => WithClientAsync(async client =>
        {
            var status = await client.UploadStream(content, remotePath, FtpRemoteExists.Overwrite,
                createRemoteDir: _options.EnsureRemotePathExists, progress: null, token: cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Failed)
                throw new Abstractions.Exceptions.StorageException($"FTP upload of '{remotePath}' failed.");
            return remotePath;
        }, cancellationToken);

    protected override Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => WithClientAsync<Stream?>(async client =>
        {
            if (!await client.FileExists(location.Location, cancellationToken).ConfigureAwait(false))
                return null;

            // Download fully: the shared connection cannot stay busy while the caller reads.
            var buffer = SpooledContent.CreateTempFile();
            try
            {
                if (!await client.DownloadStream(buffer, location.Location, token: cancellationToken).ConfigureAwait(false))
                    throw new Abstractions.Exceptions.StorageException($"FTP download of '{location.Location}' failed.");
                buffer.Seek(0, SeekOrigin.Begin);
                return buffer;
            }
            catch
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }, cancellationToken);

    protected override Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => WithClientAsync(client => client.FileExists(location.Location, cancellationToken), cancellationToken);

    protected override Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => WithClientAsync(async client =>
        {
            if (await client.FileExists(location.Location, cancellationToken).ConfigureAwait(false))
                await client.DeleteFile(location.Location, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<T> WithClientAsync<T>(Func<AsyncFtpClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
                await _client.Connect(cancellationToken).ConfigureAwait(false);
            return await operation(_client).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _client.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
