namespace KitStack.Abstractions.Exceptions;

/// <summary>
/// Represents configuration issues detected while initializing or using storage providers.
/// </summary>
public class StorageConfigurationException : StorageException
{
    public StorageConfigurationException()
    {
    }

    public StorageConfigurationException(string message) : base(message)
    {
    }

    public StorageConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
