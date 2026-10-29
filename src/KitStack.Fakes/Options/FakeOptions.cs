using KitStack.Abstractions.Options;

namespace KitStack.Fakes.Options;

/// <summary>
/// Options for fake storage behavior used in tests / local dev.
/// </summary>
public class FakeOptions
{
    /// <summary>
    /// If set, the fake will throw when a create exceeds this size (bytes). Null = no limit.
    /// Applied on top of <c>Storage:Upload:MaxBytes</c> (the smaller limit wins).
    /// </summary>
    public long? MaxFileSizeBytes { get; set; }

    /// <summary>
    /// Simulate a small delay for operations (ms). Useful to test congestion/timeouts.
    /// </summary>
    public int OperationDelayMs { get; set; }

    /// <summary>
    /// Whether the fake should preserve file content in memory across requests (default true).
    /// </summary>
    public bool PreserveInMemory { get; set; } = true;

    /// <summary>
    /// Image variants created by <c>CreateWithVariantsAsync</c>. Defaults match the other providers
    /// (1200px compressed copy and 200px thumbnail).
    /// </summary>
    public ImageProcessingOptions ImageProcessing { get; set; } = new ImageProcessingOptions();
}
