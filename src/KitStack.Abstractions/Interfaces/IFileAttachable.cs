namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Optional interface for domain entities that can hold file attachments.
/// Implement this on your entity classes to receive file entries directly from storage helpers.
/// </summary>
public interface IFileAttachable
{
    /// <summary>
    /// Add a file attachment to the entity (should not persist by itself).
    /// Implementations typically add to an in-memory collection; persistence is the caller's responsibility.
    /// </summary>
    void AddFileAttachment(IFileEntry fileEntry);
}
