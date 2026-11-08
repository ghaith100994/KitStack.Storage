namespace KitStack.Storage.Abstractions.Exceptions;

/// <summary>No storage instance with the requested name is registered or defined by the <c>IStorageInstanceSource</c>.</summary>
public class StorageInstanceNotFoundException : StorageConfigurationException
{
    public StorageInstanceNotFoundException()
    {
    }

    public StorageInstanceNotFoundException(string message) : base(message)
    {
    }

    public StorageInstanceNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The requested instance name.</summary>
    public string? StorageName { get; init; }
}

/// <summary>
/// A storage instance definition is invalid (unknown provider type, unresolved secret, invalid options).
/// Messages never contain resolved secret values.
/// </summary>
public class StorageInstanceValidationException : StorageConfigurationException
{
    public StorageInstanceValidationException()
    {
    }

    public StorageInstanceValidationException(string message) : base(message)
    {
    }

    public StorageInstanceValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Create the exception for <paramref name="storageName"/> with its validation errors.</summary>
    public StorageInstanceValidationException(string storageName, IReadOnlyList<string> errors)
        : base($"Storage instance '{storageName}' is invalid: {string.Join(" ", errors ?? [])}")
    {
        StorageName = storageName;
        Errors = errors ?? [];
    }

    /// <summary>The instance name.</summary>
    public string? StorageName { get; }

    /// <summary>Individual validation errors.</summary>
    public IReadOnlyList<string> Errors { get; } = [];
}
