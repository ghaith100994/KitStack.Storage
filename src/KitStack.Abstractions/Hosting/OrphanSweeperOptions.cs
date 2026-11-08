namespace KitStack.Abstractions.Hosting;

/// <summary>Options for the background orphan sweeper (bound from Storage:OrphanSweeper).</summary>
public sealed class OrphanSweeperOptions
{
    /// <summary>Time between sweeps. Default 1 hour.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Grace period: a file must be at least this old (and unlinked) before it is deleted, so uploads whose
    /// owning entity has not been saved yet are not swept. Default 24 hours.
    /// </summary>
    public TimeSpan MinAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Maximum files deleted per sweep. Default 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Instances to sweep (each with its own batch). Empty (default): every instance in one batch.</summary>
    public IList<string> StorageNames { get; } = [];
}
