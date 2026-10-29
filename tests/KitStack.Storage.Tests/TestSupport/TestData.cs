using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace KitStack.Storage.Tests;

internal static class TestData
{
    public static byte[] Png(int width = 400, int height = 300)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 30, 30));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    public static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    public static byte[] Text(string value = "hello, storage") => Encoding.UTF8.GetBytes(value);

    public static byte[] Random(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
    }
}

/// <summary>Entity types used as the storage folder name.</summary>
public sealed class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
