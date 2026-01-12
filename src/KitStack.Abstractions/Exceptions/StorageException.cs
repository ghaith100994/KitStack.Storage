namespace KitStack.Abstractions.Exceptions;

/// <summary>
/// Generic storage-related exception.
/// </summary>
public class StorageException : Exception
{
    public StorageException()
    {
    }

    public StorageException(string message) : base(message)
    {
    }

    public StorageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
