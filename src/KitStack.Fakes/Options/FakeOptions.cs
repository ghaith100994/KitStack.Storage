namespace KitStack.Fakes.Options;

/// <summary>
/// Options for fake storage behavior used in tests / local dev.
/// </summary>
public class FakeOptions
{
    /// <summary>
    /// If set, the fake will throw when a create exceeds this size (bytes). Null = no limit.
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
}