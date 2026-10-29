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
    /// Base URL under which the application serves stored files, for example
    /// <c>https://app.example.com/files</c> when using <c>MapKitStackStorageFiles("/files")</c> from KitStack.AspNetCore.
    /// <c>GetUrlAsync</c> returns <c>{PublicBaseUrl}/{FileLocation}</c>; when empty it returns <c>null</c>.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Serve the base directory as anonymous static files from <c>UseKitStackStorage</c> (the 0.1 behaviour).
    /// Off by default: anyone who knows or guesses a path can download the file. Prefer the authorised
    /// <c>MapKitStackStorageFiles</c> endpoint.
    /// </summary>
    public bool ServeAnonymously { get; set; }

    /// <summary>
    /// Options controlling image processing (thumbnails, compressed copies, other sizes).
    /// </summary>
    public ImageProcessingOptions ImageProcessing { get; set; } = new ImageProcessingOptions();
}
