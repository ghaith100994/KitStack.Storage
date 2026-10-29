namespace KitStack.Abstractions.IO;

/// <summary>
/// Read-only stream that replays bytes already consumed from <c>inner</c> before continuing with it.
/// Used to peek at a header of a non-seekable stream without losing those bytes.
/// </summary>
internal sealed class PrefixedReadStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < prefix.Length)
            return CopyPrefix(buffer);
        return inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < prefix.Length)
            return ValueTask.FromResult(CopyPrefix(buffer.Span));
        return inner.ReadAsync(buffer, cancellationToken);
    }

    private int CopyPrefix(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
        prefix.AsSpan(_prefixPosition, count).CopyTo(buffer);
        _prefixPosition += count;
        return count;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
