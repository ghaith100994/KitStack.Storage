namespace KitStack.Storage.Local.Options;

/// <summary>
/// Options for the local filesystem provider (bound from Storage:Local)
/// </summary>
public class LocalOptions
{
    /// <summary>
    /// Base path where files are stored. Can be absolute or relative (relative to current directory).
    /// Default: "Files" under current directory.
    /// </summary>
    public string Path { get; set; } = "Files";

    /// <summary>
    /// If true, create the base directory on startup if it does not exist.
    /// </summary>
    public bool EnsureBasePathExists { get; set; } = true;

    /// <summary>
    /// Options controlling image processing (thumbnails, compressed copies, other sizes).
    /// </summary>
    public ImageProcessingOptions ImageProcessing { get; set; } = new ImageProcessingOptions();
}

/// <summary>
/// Image processing options used by the local provider to generate derivatives.
/// </summary>
public class ImageProcessingOptions
{
    /// <summary>
    /// Create a thumbnail copy for uploaded images.
    /// </summary>
    public bool CreateThumbnail { get; set; } = true;

    /// <summary>
    /// Thumbnail maximum width (px).
    /// </summary>
    public int ThumbnailMaxWidth { get; set; } = 200;

    /// <summary>
    /// Thumbnail maximum height (px).
    /// </summary>
    public int ThumbnailMaxHeight { get; set; } = 200;

    /// <summary>
    /// Create a compressed/normalized copy for uploaded images.
    /// </summary>
    public bool CreateCompressed { get; set; } = true;

    /// <summary>
    /// Maximum width for compressed image. If the image is larger, it will be downscaled to fit.
    /// </summary>
    public int CompressedMaxWidth { get; set; } = 1200;

    /// <summary>
    /// Maximum height for compressed image.
    /// </summary>
    public int CompressedMaxHeight { get; set; } = 1200;

    /// <summary>
    /// JPEG quality for compressed/thumbnail variants (0-100).
    /// </summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// Additional custom sizes to create. Each entry will be stored under a folder named after SizeName.
    /// </summary>
    public IList<ImageSizeOption> AdditionalSizes { get; set; } = [];
}

public class ImageSizeOption
{
    public string SizeName { get; set; } = string.Empty;
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
    public int JpegQuality { get; set; } = 80;
}
