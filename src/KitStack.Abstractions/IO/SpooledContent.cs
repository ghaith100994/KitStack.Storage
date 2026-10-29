using System.Security.Cryptography;
using KitStack.Abstractions.Exceptions;

namespace KitStack.Abstractions.IO;

/// <summary>
/// A seekable copy of an input stream, kept in memory up to a threshold and in a self-deleting temp file
/// beyond it. While copying it computes the SHA-256 checksum, captures the leading bytes for content-type
/// sniffing and enforces a size limit, so the source is read exactly once.
/// </summary>
public sealed class SpooledContent : IAsyncDisposable, IDisposable
{
    /// <summary>Content up to this size stays in memory.</summary>
    public const int MemoryThreshold = 4 * 1024 * 1024;

    private SpooledContent(Stream stream, long length, string checksum, byte[] header)
    {
        Stream = stream;
        Length = length;
        Checksum = checksum;
        Header = header;
    }

    /// <summary>The buffered content. Position is 0 after creation.</summary>
    public Stream Stream { get; }

    /// <summary>Content length in bytes.</summary>
    public long Length { get; }

    /// <summary>Lower-case hex SHA-256 of the content.</summary>
    public string Checksum { get; }

    /// <summary>The first bytes of the content (up to 64).</summary>
    public byte[] Header { get; }

    /// <summary>Rewind <see cref="Stream"/> to the start and return it.</summary>
    public Stream Rewind()
    {
        Stream.Seek(0, SeekOrigin.Begin);
        return Stream;
    }

    /// <summary>Copy <paramref name="source"/> into a new spool.</summary>
    /// <param name="source">Stream to read to the end. It is not disposed.</param>
    /// <param name="maxBytes">Maximum allowed length; 0 or less means unlimited.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="StorageValidationException">The content is longer than <paramref name="maxBytes"/>.</exception>
    public static async Task<SpooledContent> CreateAsync(Stream source, long maxBytes = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stream target = new MemoryStream();
        var header = new byte[64];
        var headerLength = 0;
        long total = 0;
        var buffer = new byte[81920];

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (maxBytes > 0 && total > maxBytes)
                    throw new StorageValidationException($"File exceeds the maximum allowed size of {maxBytes} bytes.");

                if (headerLength < header.Length)
                {
                    var take = Math.Min(read, header.Length - headerLength);
                    buffer.AsSpan(0, take).CopyTo(header.AsSpan(headerLength));
                    headerLength += take;
                }

                hash.AppendData(buffer, 0, read);

                if (target is MemoryStream memory && memory.Length + read > MemoryThreshold)
                {
                    var file = CreateTempFile();
                    memory.Seek(0, SeekOrigin.Begin);
                    await memory.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                    await memory.DisposeAsync().ConfigureAwait(false);
                    target = file;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Seek(0, SeekOrigin.Begin);
            return new SpooledContent(target, total, Convert.ToHexStringLower(hash.GetHashAndReset()), header[..headerLength]);
        }
        catch
        {
            await target.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Create an empty seekable stream that spills to a self-deleting temp file. Use it as the destination
    /// for content of unknown size (decryption, downloads) that must be handed back as a stream.
    /// </summary>
    public static Stream CreateTempFile()
        => new FileStream(
            Path.Combine(Path.GetTempPath(), $"kitstack-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    /// <inheritdoc/>
    public void Dispose() => Stream.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
