using KitStack.Abstractions.Options;

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
