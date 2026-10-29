using System.IO.Compression;
using KitStack.Abstractions.Utilities;

namespace KitStack.Storage.Tests;

public class ContentTypeResolverTests
{
    [Fact]
    public void Png_bytes_resolve_to_png_even_when_named_jpg()
        => Assert.Equal("image/png", ContentTypeResolver.Resolve(TestData.Png(), ".jpg"));

    [Fact]
    public void Html_named_jpg_is_not_reported_as_an_image()
        => Assert.Equal(ContentTypes.OctetStream, ContentTypeResolver.Resolve(TestData.Text("<html><script>alert(1)</script></html>"), ".jpg"));

    [Fact]
    public void Pdf_signature_is_detected()
        => Assert.Equal("application/pdf", ContentTypeResolver.Resolve(TestData.Pdf(), ".bin"));

    [Fact]
    public void Text_formats_fall_back_to_the_extension()
    {
        Assert.Equal("text/plain", ContentTypeResolver.Resolve(TestData.Text(), ".txt"));
        Assert.Equal("text/csv", ContentTypeResolver.Resolve(TestData.Text("a,b\n1,2\n"), "csv"));
    }

    [Fact]
    public void Binary_content_claiming_to_be_text_is_octet_stream()
        => Assert.Equal(ContentTypes.OctetStream, ContentTypeResolver.Resolve([0x00, 0x01, 0x02, 0x03], ".txt"));

    [Fact]
    public void Office_documents_are_recognised_from_the_zip_signature()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("[Content_Types].xml");
        var bytes = ms.ToArray();

        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ContentTypeResolver.Resolve(bytes, ".docx"));
        Assert.Equal("application/zip", ContentTypeResolver.Resolve(bytes, ".exe"));
    }

    [Fact]
    public void Text_starting_with_BM_is_not_a_bitmap()
        => Assert.Equal("text/plain", ContentTypeResolver.Resolve(TestData.Text("BMW price list for 2026"), ".txt"));

    [Fact]
    public void Unknown_extension_without_signature_is_octet_stream()
        => Assert.Equal(ContentTypes.OctetStream, ContentTypeResolver.Resolve(TestData.Text(), ".xyz"));
}
