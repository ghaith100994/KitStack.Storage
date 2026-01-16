namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Optional non-generic hook: managers can implement this to receive runtime option updates.
/// </summary>
public interface IConfigurableProvider
{
    /// <summary>
    /// Apply new options at runtime (best-effort).
    /// </summary>
    void UpdateOptions(object options);
}

/// <summary>
/// Optional strongly-typed hook: implement this if you want typed option updates.
/// </summary>
public interface IConfigurableProvider<TOptions>
{
    /// <summary>
    /// Apply new options at runtime (best-effort).
    /// </summary>
    void UpdateOptions(TOptions options);
}
