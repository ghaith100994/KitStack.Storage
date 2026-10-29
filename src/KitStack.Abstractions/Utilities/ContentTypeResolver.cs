using System.Text;

namespace KitStack.Abstractions.Utilities;

/// <summary>
/// Resolves a file's content type from its leading bytes and its extension, without trusting the
/// content type a client declared. Formats that have a binary signature are only reported when the
/// signature is present: a ".jpg" that does not start with a JPEG header resolves to
/// <see cref="ContentTypes.OctetStream"/>.
/// </summary>
public static class ContentTypeResolver
{
    /// <summary>Number of leading bytes <see cref="Resolve"/> looks at.</summary>
    public const int HeaderLength = 64;

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
        [".ico"] = "image/x-icon", [".heic"] = "image/heic", [".heif"] = "image/heif", [".avif"] = "image/avif",
        [".svg"] = "image/svg+xml",
        // Documents
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword", [".xls"] = "application/vnd.ms-excel", [".ppt"] = "application/vnd.ms-powerpoint",
        [".msg"] = "application/vnd.ms-outlook",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text", [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".rtf"] = "application/rtf",
        // Text
        [".txt"] = "text/plain", [".csv"] = "text/csv", [".md"] = "text/markdown", [".json"] = "application/json",
        [".xml"] = "application/xml", [".html"] = "text/html", [".htm"] = "text/html", [".css"] = "text/css",
        [".js"] = "text/javascript", [".eml"] = "message/rfc822",
        // Archives
        [".zip"] = "application/zip", [".gz"] = "application/gzip", [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        // Audio / video
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg", [".m4a"] = "audio/mp4",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".avi"] = "video/x-msvideo", [".webm"] = "video/webm",
    };

    // Extensions whose format has a binary signature: never reported from the extension alone.
    private static readonly HashSet<string> SignatureRequired = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif",
        ".pdf", ".doc", ".xls", ".ppt", ".msg", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp",
        ".zip", ".gz", ".7z", ".rar", ".mp3", ".wav", ".ogg", ".m4a", ".mp4", ".mov", ".avi", ".webm",
    };

    private static readonly HashSet<string> OoxmlOrOdf = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp",
    };

    private static readonly HashSet<string> OleCompound = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".xls", ".ppt", ".msg",
    };

    /// <summary>
    /// Resolve the content type of a file.
    /// </summary>
    /// <param name="header">The first bytes of the file (up to <see cref="HeaderLength"/>).</param>
    /// <param name="extension">File extension, with or without the leading dot. May be empty.</param>
    public static string Resolve(ReadOnlySpan<byte> header, string? extension)
    {
        var ext = NormalizeExtension(extension);
        var sniffed = Sniff(header, ext);
        if (sniffed is not null)
            return sniffed;

        if (ext.Length == 0 || SignatureRequired.Contains(ext))
            return ContentTypes.OctetStream;

        if (!ByExtension.TryGetValue(ext, out var byExtension))
            return ContentTypes.OctetStream;

        // Text formats have no signature; at least refuse binary content claiming to be text.
        return LooksLikeText(header) ? byExtension : ContentTypes.OctetStream;
    }

    /// <summary>
    /// Map an extension to a content type without looking at content (for serving files whose bytes
    /// are not at hand). Unknown extensions map to <see cref="ContentTypes.OctetStream"/>.
    /// </summary>
    public static string FromExtension(string? extension)
        => ByExtension.TryGetValue(NormalizeExtension(extension), out var type) ? type : ContentTypes.OctetStream;

    private static string? Sniff(ReadOnlySpan<byte> h, string ext)
    {
        if (StartsWith(h, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (StartsWith(h, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (StartsWithAscii(h, "GIF87a") || StartsWithAscii(h, "GIF89a")) return "image/gif";
        // "BM" alone is too weak (text can start with it); bytes 6-9 of a BMP header are reserved zeros.
        if (StartsWithAscii(h, "BM") && h.Length >= 14 && h.Slice(6, 4).SequenceEqual(new byte[4])) return "image/bmp";
        if (StartsWith(h, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(h, [0x4D, 0x4D, 0x00, 0x2A])) return "image/tiff";
        if (StartsWith(h, [0x00, 0x00, 0x01, 0x00]) && ext == ".ico") return "image/x-icon";
        if (StartsWithAscii(h, "%PDF-")) return "application/pdf";

        if (StartsWithAscii(h, "RIFF") && h.Length >= 12)
        {
            var form = h.Slice(8, 4);
            if (form.SequenceEqual("WEBP"u8)) return "image/webp";
            if (form.SequenceEqual("WAVE"u8)) return "audio/wav";
            if (form.SequenceEqual("AVI "u8)) return "video/x-msvideo";
        }

        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(h.Slice(8, 4));
            return brand switch
            {
                "heic" or "heix" or "heim" or "heis" => "image/heic",
                "mif1" or "msf1" => ext == ".avif" ? "image/avif" : "image/heif",
                "avif" or "avis" => "image/avif",
                "qt  " => "video/quicktime",
                "M4A " => "audio/mp4",
                _ => ext == ".m4a" ? "audio/mp4" : "video/mp4",
            };
        }

        if (StartsWith(h, [0x50, 0x4B, 0x03, 0x04]))
            return OoxmlOrOdf.Contains(ext) ? ByExtension[ext] : "application/zip";
        if (StartsWith(h, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
            return OleCompound.Contains(ext) ? ByExtension[ext] : "application/x-ole-storage";

        if (StartsWith(h, [0x1F, 0x8B])) return "application/gzip";
        if (StartsWith(h, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return "application/x-7z-compressed";
        if (StartsWithAscii(h, "Rar!")) return "application/vnd.rar";
        if (StartsWithAscii(h, "OggS")) return "audio/ogg";
        if (StartsWith(h, [0x1A, 0x45, 0xDF, 0xA3])) return "video/webm";
        if (StartsWithAscii(h, "ID3") || (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0 && ext == ".mp3")) return "audio/mpeg";
        if (StartsWithAscii(h, "{\\rtf")) return "application/rtf";

        return null;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        foreach (var b in header)
        {
            // NUL and most C0 controls (other than tab, LF, CR, FF) do not occur in text files.
            if (b == 0 || (b < 0x20 && b is not (0x09 or 0x0A or 0x0C or 0x0D)))
                return false;
        }

        return true;
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;
        var ext = extension.Trim();
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) => data.StartsWith(prefix);

    private static bool StartsWithAscii(ReadOnlySpan<byte> data, string prefix)
    {
        if (data.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
                return false;
        }

        return true;
    }
}
