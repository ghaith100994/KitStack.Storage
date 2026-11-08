namespace KitStack.Storage.Abstractions.Interfaces;

/// <summary>Implemented by managers that wrap another one (tracking, instance lifetime), so callers can reach the provider instance.</summary>
public interface IFileStorageManagerDecorator
{
    /// <summary>The wrapped manager.</summary>
    IFileStorageManager Inner { get; }
}
