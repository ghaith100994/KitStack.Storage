using System;

namespace KitStack.Abstractions.Exceptions;

/// <summary>
/// Thrown when requested file/resource is not found in the storage provider.
/// </summary>
public class StorageNotFoundException : StorageException
{
    public StorageNotFoundException()
    {
    }

    public StorageNotFoundException(string message) : base(message)
    {
    }

    public StorageNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}