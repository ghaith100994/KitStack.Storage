namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Optional non-generic hook: managers can implement this to receive runtime option updates.
/// </summary>
using KitStack.Abstractions.Models;

/// <summary>
/// Optional hook: managers can implement this to receive runtime StorageProvider updates.
/// The StorageProvider contains the typed Options object which implementations can inspect.
/// </summary>
public interface IConfigurableProvider
{
    /// <summary>
    /// Apply new provider configuration at runtime (best-effort).
    /// </summary>
    void UpdateOptions(StorageProvider provider);
}
