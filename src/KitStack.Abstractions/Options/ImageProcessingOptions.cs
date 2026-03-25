namespace KitStack.Abstractions.Options;

/// <summary>
/// Common image-processing options shared across all storage providers.
/// Providers may extend this class to add provider-specific settings (e.g. per-target routing).
/// </summary>
public class ImageProcessingOptions
{
    /// <summary>Generate a thumbnail copy for uploaded images.</summary>
    public bool CreateThumbnail { get; set; } = true;

    /// <summary>Thumbnail maximum width (px).</summary>
    public int ThumbnailMaxWidth { get; set; } = 200;

    /// <summary>Thumbnail maximum height (px).</summary>
    public int ThumbnailMaxHeight { get; set; } = 200;

    /// <summary>Generate a compressed/normalised copy for uploaded images.</summary>
    public bool CreateCompressed { get; set; } = true;

    /// <summary>Maximum width for the compressed image (px).</summary>
    public int CompressedMaxWidth { get; set; } = 1200;

    /// <summary>Maximum height for the compressed image (px).</summary>
    public int CompressedMaxHeight { get; set; } = 1200;

    /// <summary>JPEG quality (0–100) applied to all generated variants.</summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>Additional named sizes to generate.</summary>
    public IList<ImageSizeOption> AdditionalSizes { get; set; } = [];
}

/// <summary>Defines a single named image-size variant.</summary>
public class ImageSizeOption
{
    public string SizeName { get; set; } = string.Empty;
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
    public int JpegQuality { get; set; } = 80;
}
