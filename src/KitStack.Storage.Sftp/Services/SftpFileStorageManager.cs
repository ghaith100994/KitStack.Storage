using System.Collections.Concurrent;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.IO;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using KitStack.Abstractions.Utilities;
using KitStack.Storage.Sftp.Options;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KitStack.Storage.Sftp.Services;

/// <summary>
/// SFTP implementation of <c>IFileStorageManager</c> built on SSH.NET.
/// One connection is shared and operations are serialized on it.
/// </summary>
public sealed class SftpFileStorageManager : FileStorageManagerBase, IDisposable
{
    private readonly SftpOptions _options;
    private readonly SftpClient _client;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _knownDirectories = new(StringComparer.Ordinal);
    private bool _disposed;

    protected override string StorageProvider => "Sftp";

    public SftpFileStorageManager(
        IOptions<SftpOptions> options,
        IOptions<StorageOptions>? storageOptions = null,
        IFileContentEncryptor? encryptor = null)
        : base(storageOptions?.Value, encryptor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        var remote = string.IsNullOrWhiteSpace(_options.RemotePath) ? "Files" : _options.RemotePath.Trim();
        _root = remote.Length > 1 ? remote.TrimEnd('/') : remote;
        _client = SftpClientFactory.Create(_options);
    }

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _options.ImageProcessing;

    protected override Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var remotePath = $"{Join(_root, request.Folder)}/{request.FileName}";
        return UploadAsync(remotePath, request.Content, cancellationToken);
    }

    protected override Task OverwriteCoreAsync(StorageLocation location, Stream content, long length, string contentType, CancellationToken cancellationToken)
        => UploadAsync(location.Location, content, cancellationToken);

    private Task<string> UploadAsync(string remotePath, Stream content, CancellationToken cancellationToken)
        => WithClientAsync(async client =>
        {
            var slash = remotePath.LastIndexOf('/');
            if (_options.EnsureRemotePathExists && slash > 0)
                await EnsureDirectoryAsync(client, remotePath[..slash], cancellationToken).ConfigureAwait(false);
            await client.UploadFileAsync(content, remotePath, canOverride: true, uploadProgress: null, cancellationToken).ConfigureAwait(false);
            return remotePath;
        }, cancellationToken);

    protected override Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => WithClientAsync<Stream?>(async client =>
        {
            // Download fully: the shared connection cannot stay busy while the caller reads.
            var buffer = SpooledContent.CreateTempFile();
            try
            {
                await client.DownloadFileAsync(location.Location, buffer, cancellationToken).ConfigureAwait(false);
                buffer.Seek(0, SeekOrigin.Begin);
                return buffer;
            }
            catch (SftpPathNotFoundException)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }, cancellationToken);

    protected override Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => WithClientAsync(client => client.ExistsAsync(location.Location, cancellationToken), cancellationToken);

    protected override Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => WithClientAsync(async client =>
        {
            try
            {
                await client.DeleteFileAsync(location.Location, cancellationToken).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
                // Already gone.
            }

            return true;
        }, cancellationToken);

    private async Task EnsureDirectoryAsync(SftpClient client, string directory, CancellationToken cancellationToken)
    {
        if (_knownDirectories.ContainsKey(directory))
            return;

        // SFTP has no "mkdir -p": create each missing level.
        var current = directory.StartsWith('/') ? string.Empty : null;
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current is null ? segment : $"{current}/{segment}";
            if (_knownDirectories.ContainsKey(current))
                continue;
            if (!await client.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                await client.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
            _knownDirectories.TryAdd(current, true);
        }
    }

    private async Task<T> WithClientAsync<T>(Func<SftpClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SshConnectionException ex) when (ex.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.HostKeyNotVerifiable)
                {
                    throw new StorageConfigurationException(
                        $"SFTP host key of '{_options.Host}' does not match Storage:Sftp:HostKeyFingerprint.", ex);
                }
            }

            return await operation(_client).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // StoragePaths.Combine trims slashes, so restore the leading '/' of an absolute root.
    private static string Join(string root, string relative)
    {
        var joined = StoragePaths.Combine(root, relative);
        return root.StartsWith('/') ? "/" + joined : joined;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
        _gate.Dispose();
    }
}
