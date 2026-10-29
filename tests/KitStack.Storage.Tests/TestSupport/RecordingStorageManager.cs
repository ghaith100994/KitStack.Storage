using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;

namespace KitStack.Storage.Tests;

/// <summary>
/// Minimal provider over a dictionary that records what <see cref="FileStorageManagerBase"/> hands to providers.
/// It does not override OverwriteCoreAsync, so the base's default (not supported) can be tested.
/// </summary>
internal sealed class RecordingStorageManager(StorageOptions? options = null, IFileContentEncryptor? encryptor = null, ImageProcessingOptions? images = null)
    : FileStorageManagerBase(options, encryptor)
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<StorageWriteRequest> Writes { get; } = [];
    public List<StorageLocation> Deletes { get; } = [];

    /// <summary>Return non-seekable streams from reads, like network providers.</summary>
    public bool NonSeekableReads { get; set; }

    protected override string StorageProvider => "Recording";

    protected override ImageProcessingOptions? GetImageProcessingOptions() => images;

    protected override async Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await request.Content.CopyToAsync(ms, cancellationToken);
        Writes.Add(request);
        var location = $"{request.Folder}/{request.FileName}";
        Files[location] = ms.ToArray();
        return location;
    }

    protected override Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        if (!Files.TryGetValue(location.Location, out var bytes))
            return Task.FromResult<Stream?>(null);
        Stream stream = NonSeekableReads ? new NonSeekableStream(bytes) : new MemoryStream(bytes);
        return Task.FromResult<Stream?>(stream);
    }

    protected override Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => Task.FromResult(Files.ContainsKey(location.Location));

    protected override Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        Deletes.Add(location);
        Files.Remove(location.Location);
        return Task.CompletedTask;
    }
}

/// <summary>Read-only, forward-only stream (like an HTTP response body).</summary>
internal sealed class NonSeekableStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data);

    public bool Disposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
