namespace KitStack.Abstractions.Exceptions;

/// <summary>
/// Represents validation and user input errors when interacting with storage providers.
/// </summary>
public class StorageValidationException : StorageException
{
    public StorageValidationException()
    {
    }

    public StorageValidationException(string message) : base(message)
    {
    }

    public StorageValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
