using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Utilities;
using SixLabors.ImageSharp;

namespace KitStack.Storage.Tests.Abstractions;

public class ImageProcessingHelperTests
{
    [Theory]
    [InlineData(".jpg", true)]
    [InlineData("PNG", true)]
    [InlineData(".webp", true)]
    [InlineData(".pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_image_extensions(string? extension, bool expected)
    {
        Assert.Equal(expected, ImageProcessingHelper.IsImageExtension(extension!));
        Assert.Equal(expected ? "Images" : "Others", ImageProcessingHelper.GetFileTypeFolder(extension!));
    }

    [Fact]
    public async Task Resizes_within_bounds_keeping_aspect_ratio()
    {
        using var output = new MemoryStream();
        await ImageProcessingHelper.CreateResizedJpegToStreamAsync(new MemoryStream(TestData.Png(400, 300)), output, 100, 100, 150);

        Assert.Equal(0, output.Position);
        using var image = await Image.LoadAsync(output);
        Assert.Equal((100, 75), (image.Width, image.Height));
        Assert.Equal("JPEG", image.Metadata.DecodedImageFormat!.Name);
    }

    [Fact]
    public async Task Smaller_images_are_not_upscaled_and_the_source_is_rewound()
    {
        var source = new MemoryStream(TestData.Png(50, 40));
        source.Seek(10, SeekOrigin.Begin);
        using var output = new MemoryStream();

        await ImageProcessingHelper.CreateResizedJpegToStreamAsync(source, output, 100, 100, 0);

        using var image = await Image.LoadAsync(output);
        Assert.Equal((50, 40), (image.Width, image.Height));
    }

    [Fact]
    public async Task Invalid_arguments_throw()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImageProcessingHelper.CreateResizedJpegToStreamAsync(new MemoryStream(), new MemoryStream(), 0, 10, 80));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImageProcessingHelper.CreateResizedJpegToStreamAsync(new MemoryStream(), new MemoryStream(), 10, -1, 80));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ImageProcessingHelper.CreateResizedJpegToStreamAsync(null!, new MemoryStream(), 10, 10, 80));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ImageProcessingHelper.CreateResizedJpegToStreamAsync(new MemoryStream(), null!, 10, 10, 80));
    }
}

public class ContentTypeResolverSignatureTests
{
    public static TheoryData<byte[], string, string> Signatures => new()
    {
        { "GIF89a......"u8.ToArray(), ".bin", "image/gif" },
        { "GIF87a......"u8.ToArray(), ".gif", "image/gif" },
        { Concat("RIFF"u8, [0, 0, 0, 0], "WEBP"u8), ".webp", "image/webp" },
        { Concat("RIFF"u8, [0, 0, 0, 0], "WAVE"u8), ".wav", "audio/wav" },
        { Concat("RIFF"u8, [0, 0, 0, 0], "AVI "u8), ".avi", "video/x-msvideo" },
        { [0x49, 0x49, 0x2A, 0x00, 1, 2], ".tif", "image/tiff" },
        { [0x4D, 0x4D, 0x00, 0x2A, 1, 2], ".tiff", "image/tiff" },
        { [0x00, 0x00, 0x01, 0x00, 1, 2], ".ico", "image/x-icon" },
        { Concat("BM"u8, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), ".bmp", "image/bmp" },
        { Concat([0, 0, 0, 0x18], "ftypheic"u8), ".heic", "image/heic" },
        { Concat([0, 0, 0, 0x18], "ftypmif1"u8), ".heif", "image/heif" },
        { Concat([0, 0, 0, 0x18], "ftypmif1"u8), ".avif", "image/avif" },
        { Concat([0, 0, 0, 0x18], "ftypavif"u8), ".bin", "image/avif" },
        { Concat([0, 0, 0, 0x18], "ftypqt  "u8), ".mov", "video/quicktime" },
        { Concat([0, 0, 0, 0x18], "ftypM4A "u8), ".bin", "audio/mp4" },
        { Concat([0, 0, 0, 0x18], "ftypisom"u8), ".m4a", "audio/mp4" },
        { Concat([0, 0, 0, 0x18], "ftypisom"u8), ".mp4", "video/mp4" },
        { [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], ".doc", "application/msword" },
        { [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], ".bin", "application/x-ole-storage" },
        { [0x1F, 0x8B, 0x08, 0x00], ".gz", "application/gzip" },
        { [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00], ".7z", "application/x-7z-compressed" },
        { "Rar!\u001a\u0007"u8.ToArray(), ".rar", "application/vnd.rar" },
        { "OggS\0\0"u8.ToArray(), ".ogg", "audio/ogg" },
        { [0x1A, 0x45, 0xDF, 0xA3, 0x01], ".webm", "video/webm" },
        { "ID3\u0003\0"u8.ToArray(), ".bin", "audio/mpeg" },
        { [0xFF, 0xFB, 0x90, 0x00], ".mp3", "audio/mpeg" },
        { "{\\rtf1\\ansi"u8.ToArray(), ".doc", "application/rtf" },
        { "{\"a\":1}"u8.ToArray(), "json", "application/json" },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Detects_signatures(byte[] header, string extension, string expected)
        => Assert.Equal(expected, ContentTypeResolver.Resolve(header, extension));

    [Theory]
    [InlineData(".pdf", "application/pdf")]
    [InlineData("svg", "image/svg+xml")]
    [InlineData(".unknown", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void Maps_extensions_without_content(string? extension, string expected)
        => Assert.Equal(expected, ContentTypeResolver.FromExtension(extension));

    [Fact]
    public void Empty_content_without_extension_is_octet_stream()
        => Assert.Equal("application/octet-stream", ContentTypeResolver.Resolve([], null));

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Concat(ReadOnlySpan<byte> a, byte[] b) => [.. a, .. b];
    private static byte[] Concat(byte[] a, ReadOnlySpan<byte> b) => [.. a, .. b];
    private static byte[] Concat(ReadOnlySpan<byte> a, byte[] b, ReadOnlySpan<byte> c) => [.. a, .. b, .. c];
}

public class EncryptionFormatTests
{
    [Fact]
    public void Header_detection_and_options()
    {
        var encryptor = new AesGcmFileContentEncryptor(TestData.NewKey(), [" ", TestData.NewKey()]);
        Assert.Equal(6, encryptor.HeaderLength);
        Assert.False(encryptor.IsEncrypted("KSE"u8));
        Assert.True(encryptor.IsEncrypted("KSENC\u0001rest"u8));

        Assert.Null(AesGcmFileContentEncryptor.FromOptions(null));
        Assert.Null(AesGcmFileContentEncryptor.FromOptions(new StorageOptions { MasterEncryptionKey = " " }));
        Assert.NotNull(AesGcmFileContentEncryptor.FromOptions(new StorageOptions { MasterEncryptionKey = TestData.NewKey() }));
        Assert.Throws<StorageConfigurationException>(() => new AesGcmFileContentEncryptor(TestData.NewKey(), ["bad!"]));
    }

    [Fact]
    public async Task Rejects_plain_or_corrupt_input()
    {
        var encryptor = new AesGcmFileContentEncryptor(TestData.NewKey());
        await Assert.ThrowsAsync<StorageException>(() => encryptor.DecryptAsync(new MemoryStream(TestData.Text()), new MemoryStream()));

        using var ok = new MemoryStream();
        await encryptor.EncryptAsync(new MemoryStream(TestData.Random(10)), ok);
        var bytes = ok.ToArray();

        var badChunkSize = (byte[])bytes.Clone();
        badChunkSize[26] = 0x7F; // chunk size far above the limit
        await Assert.ThrowsAsync<StorageException>(() => encryptor.DecryptAsync(new MemoryStream(badChunkSize), new MemoryStream()));

        var badFlag = (byte[])bytes.Clone();
        badFlag[30] = 7; // final flag must be 0 or 1
        await Assert.ThrowsAsync<StorageException>(() => encryptor.DecryptAsync(new MemoryStream(badFlag), new MemoryStream()));

        await Assert.ThrowsAsync<StorageException>(() => encryptor.DecryptAsync(new MemoryStream(bytes[..32]), new MemoryStream()));
        await Assert.ThrowsAsync<StorageException>(() => encryptor.DecryptAsync(new MemoryStream(bytes[..^3]), new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => encryptor.EncryptAsync(null!, new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => encryptor.DecryptAsync(new MemoryStream(), null!));
    }

    [Fact]
    public async Task Each_encryption_uses_a_fresh_salt()
    {
        var encryptor = new AesGcmFileContentEncryptor(TestData.NewKey());
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        await encryptor.EncryptAsync(new MemoryStream(TestData.Text()), a);
        await encryptor.EncryptAsync(new MemoryStream(TestData.Text()), b);
        Assert.NotEqual(a.ToArray(), b.ToArray());
    }
}
