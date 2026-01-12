namespace KitStack.Storage.Local.Options;

/// <summary>
/// Options for the local filesystem storage provider. Bound from configuration section Storage:Local.
/// </summary>
public class LocalOptions
{
    /// <summary>
    /// Relative or absolute path where files will be stored. 
    /// If relative, it is combined with Directory.GetCurrentDirectory().
    /// </summary>
    public string Path { get; set; } = "Files";

    /// <summary>
    /// Optional: decide whether to create the base directory when the provider starts.
    /// </summary>
    public bool EnsureBasePathExists { get; set; } = true;
}