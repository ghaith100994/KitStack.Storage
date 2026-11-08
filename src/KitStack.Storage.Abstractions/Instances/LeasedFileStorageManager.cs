using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KitStack.Storage.Abstractions.Instances;

/// <summary>
/// One build of an instance. Every operation holds a lease (and a read stream holds one until it is disposed); once
/// the build is retired, new calls are forwarded to the current build and the inner manager is disposed exactly once
/// when the last lease is released.
/// </summary>
internal sealed class LeasedFileStorageManager(
    string name,
    IFileStorageManager inner,
    Func<CancellationToken, ValueTask<LeasedFileStorageManager>> current,
    ILogger logger) : IFileStorageManager, IStorageContentAccess, IFileStorageManagerDecorator
{
    private readonly Lock _gate = new();
    private int _leases;
    private bool _retired;
    private int _disposed;
    private Task _disposal = Task.CompletedTask;

    public IFileStorageManager Inner => inner;

    public string StorageName => inner.StorageName;

    public bool IsRetired
    {
        get
        {
            lock (_gate)
                return _retired;
        }
    }

    /// <summary>Completes when the inner manager has been disposed (after retirement and the last lease).</summary>
    public Task Disposal => _disposal;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    private bool TryAcquire()
    {
        lock (_gate)
        {
            if (_retired)
                return false;
            _leases++;
            return true;
        }
    }

    private void Release()
    {
        bool dispose;
        lock (_gate)
        {
            _leases--;
            dispose = _retired && _leases == 0;
        }

        if (dispose)
            DisposeInner();
    }

    public void Retire()
    {
        bool dispose;
        lock (_gate)
        {
            if (_retired)
                return;
            _retired = true;
            dispose = _leases == 0;
        }

        if (dispose)
            DisposeInner();
    }

    private void DisposeInner()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            switch (inner)
            {
                case IAsyncDisposable asyncDisposable:
                    _disposal = Observe(asyncDisposable.DisposeAsync().AsTask());
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Disposing a replaced build of storage instance {StorageName} failed", name);
        }
    }

    private async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Disposing a replaced build of storage instance {StorageName} failed", name);
        }
    }

    private async ValueTask<(IFileStorageManager Manager, LeasedFileStorageManager Owner)> AcquireAsync(CancellationToken cancellationToken)
    {
        var handle = this;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (handle.TryAcquire())
                return (handle.Inner, handle);
            handle = await current(cancellationToken).ConfigureAwait(false);
        }

        throw new ObjectDisposedException($"Storage instance '{name}'");
    }

    private async Task<T> RunAsync<T>(Func<IFileStorageManager, Task<T>> operation, CancellationToken cancellationToken)
    {
        var (manager, owner) = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(manager).ConfigureAwait(false);
        }
        finally
        {
            owner.Release();
        }
    }

    private Task RunAsync(Func<IFileStorageManager, Task> operation, CancellationToken cancellationToken)
        => RunAsync(async m =>
        {
            await operation(m).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class
        => RunAsync(m => m.CreateAsync<T>(file, category, cancellationToken), cancellationToken);

    public Task<IFileEntry> CreateAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default) where T : class
        => RunAsync(m => m.CreateAsync<T>(content, fileName, contentType, category, cancellationToken), cancellationToken);

    public Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class, IFileAttachable
        => RunAsync(m => m.CreateAsync(entity, file, category, cancellationToken), cancellationToken);

    public Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class
        => RunAsync(m => m.CreateWithVariantsAsync<T>(file, category, cancellationToken), cancellationToken);

    public Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default) where T : class
        => RunAsync(m => m.CreateWithVariantsAsync<T>(content, fileName, contentType, category, cancellationToken), cancellationToken);

    public Task<IFileEntry> CreateAsync(StorageWriteOptions options, Stream content, CancellationToken cancellationToken = default)
        => RunAsync(m => m.CreateAsync(options, content, cancellationToken), cancellationToken);

    public Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync(StorageWriteOptions options, Stream content, CancellationToken cancellationToken = default)
        => RunAsync(m => m.CreateWithVariantsAsync(options, content, cancellationToken), cancellationToken);

    public async Task<Stream> OpenReadAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        var (manager, owner) = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await manager.OpenReadAsync(entry, cancellationToken).ConfigureAwait(false);
            // The lease moves to the stream: the build is not disposed while the caller still reads.
            return new LeaseStream(stream, owner.Release);
        }
        catch
        {
            owner.Release();
            throw;
        }
    }

    public Task<bool> ExistsAsync(IFileEntry entry, CancellationToken cancellationToken = default)
        => RunAsync(m => m.ExistsAsync(entry, cancellationToken), cancellationToken);

    public Task DeleteAsync(IFileEntry entry, CancellationToken cancellationToken = default)
        => RunAsync(m => m.DeleteAsync(entry, cancellationToken), cancellationToken);

    public Task OverwriteAsync(IFileEntry entry, Stream content, CancellationToken cancellationToken = default)
        => RunAsync(m => m.OverwriteAsync(entry, content, cancellationToken), cancellationToken);

    public Task<Uri?> GetUrlAsync(IFileEntry entry, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
        => RunAsync(m => m.GetUrlAsync(entry, lifetime, cancellationToken), cancellationToken);

    public string? PredictLocation(string folder, string fileName, string variantType)
        => (inner as IStorageContentAccess)?.PredictLocation(folder, fileName, variantType);

    public Task<StoredContent> PutContentAsync(StorageContentWrite request, Stream plaintext, CancellationToken cancellationToken = default)
        => RunAsync(m => ContentAccess(m).PutContentAsync(request, plaintext, cancellationToken), cancellationToken);

    public Task<StoredContent?> VerifyContentAsync(string location, string variantType, CancellationToken cancellationToken = default)
        => RunAsync(m => ContentAccess(m).VerifyContentAsync(location, variantType, cancellationToken), cancellationToken);

    public Task DeleteContentAsync(string location, string variantType, CancellationToken cancellationToken = default)
        => RunAsync(m => ContentAccess(m).DeleteContentAsync(location, variantType, cancellationToken), cancellationToken);

    private static IStorageContentAccess ContentAccess(IFileStorageManager manager)
        => manager as IStorageContentAccess
           ?? throw new NotSupportedException($"{manager.GetType().Name} does not support direct content access.");

    /// <summary>Read stream that releases its lease when disposed.</summary>
    private sealed class LeaseStream(Stream inner, Action release) : Stream
    {
        private int _released;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                ReleaseOnce();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            ReleaseOnce();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release();
        }
    }
}
