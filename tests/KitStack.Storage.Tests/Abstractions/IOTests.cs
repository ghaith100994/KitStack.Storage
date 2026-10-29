using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.IO;

namespace KitStack.Storage.Tests.Abstractions;

[Collection(TempFileCollection.Name)]
public class SpooledContentTests
{
    [Fact]
    public async Task Small_content_stays_in_memory_with_checksum_and_header()
    {
        var data = TestData.Random(1000);

        await using var spool = await SpooledContent.CreateAsync(new MemoryStream(data));

        Assert.IsType<MemoryStream>(spool.Stream);
        Assert.Equal(1000, spool.Length);
        Assert.Equal(TestData.Sha256(data), spool.Checksum);
        Assert.Equal(data[..64], spool.Header);
        Assert.Equal(0, spool.Stream.Position);
    }

    [Fact]
    public async Task Large_content_spills_to_a_temp_file_that_is_deleted_on_dispose()
    {
        var data = TestData.Random(SpooledContent.MemoryThreshold + 10);

        string path;
        await using (var spool = await SpooledContent.CreateAsync(new MemoryStream(data)))
        {
            var file = Assert.IsType<FileStream>(spool.Stream);
            path = file.Name;
            Assert.True(File.Exists(path));
            Assert.Equal(TestData.Sha256(data), spool.Checksum);
            Assert.Equal(data, await ReadAsync(spool.Rewind()));
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Empty_content_has_the_empty_checksum()
    {
        await using var spool = await SpooledContent.CreateAsync(new MemoryStream());
        Assert.Equal(0, spool.Length);
        Assert.Empty(spool.Header);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", spool.Checksum);
    }

    [Fact]
    public async Task Exceeding_the_limit_fails_and_leaves_no_temp_file()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "kitstack-*.tmp").Length;

        await Assert.ThrowsAsync<StorageValidationException>(() =>
            SpooledContent.CreateAsync(new MemoryStream(TestData.Random(SpooledContent.MemoryThreshold + 100)), SpooledContent.MemoryThreshold + 50));
        await using (await SpooledContent.CreateAsync(new MemoryStream(TestData.Random(10)), 10)) { }

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "kitstack-*.tmp").Length);
        await Assert.ThrowsAsync<ArgumentNullException>(() => SpooledContent.CreateAsync(null!));
    }

    private static async Task<byte[]> ReadAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}

public class PrefixedReadStreamTests
{
    [Fact]
    public async Task Replays_the_prefix_then_the_inner_stream()
    {
        var inner = new NonSeekableStream([4, 5, 6]);
        await using var stream = new PrefixedReadStream([1, 2, 3], inner);

        var one = new byte[2];
        Assert.Equal(2, stream.Read(one, 0, 2));
        var rest = new byte[10];
        var read = await stream.ReadAsync(rest);
        read += await stream.ReadAsync(rest.AsMemory(read));
        read += stream.Read(rest.AsSpan(read));

        Assert.Equal(new byte[] { 1, 2 }, one);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, rest[..read]);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.True(stream.CanRead);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Fact]
    public void Disposing_disposes_the_inner_stream()
    {
        var inner = new NonSeekableStream([1]);
        new PrefixedReadStream([], inner).Dispose();
        Assert.True(inner.Disposed);
    }
}
