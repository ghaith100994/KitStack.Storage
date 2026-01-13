using KitStack.Abstractions.Models;
using Microsoft.AspNetCore.Http;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Abstraction for file storage operations.
/// Implementations should be lightweight and provider-specific.
/// </summary>
public interface IFileStorageManager
{
    /// <summary>
    /// Create and store a file for the specified entity type <typeparamref name="T"/>.
    /// The implementation should produce a populated <see cref="IFileEntry"/> describing
    /// the stored primary/original file. Providers may also create image variants according
    /// to their configuration but this method returns the primary entry only.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the stored <see cref="IFileEntry"/>.</returns>
    Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Create and store a file associated with the provided entity instance.
    /// Implementations may attach or mutate the entity (for example adding a FileEntry)
    /// when the entity implements <see cref="IFileAttachable"/>.
    /// </summary>
    /// <typeparam name="T">Entity type which implements <see cref="IFileAttachable"/>.</typeparam>
    /// <param name="entity">The entity instance to associate the file with.</param>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the stored <see cref="IFileEntry"/>.</returns>
    Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class, IFileAttachable;

    /// <summary>
    /// Create the primary/original file and any configured image variants (thumbnail, compressed,
    /// or additional sizes). Returns the primary <see cref="IFileEntry"/> and a list of variant
    /// entries that were created by the provider.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to a tuple containing the primary entry and created variants.</returns>
    Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
    where T : class;
}