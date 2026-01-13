using System.Collections.Concurrent;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Utilities;
using System.Runtime.InteropServices;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Models;
using KitStack.Fakes.Options;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

namespace KitStack.Fakes.Services;

/// <summary>
/// In-memory implementation of IFileStorageManager used for unit tests and local development.
/// Also implements IFakeFileStore so tests can inspect the stored state.
/// </summary>
public class InMemoryFileStorageManager(IOptions<FakeOptions>? options = null) : IFileStorageManager, IFakeFileStore
{
    private readonly ConcurrentDictionary<string, FakeStoredFile> _store = new();
    private readonly FakeOptions _options = options?.Value ?? new FakeOptions();

    /// <summary>
    /// Create and store an uploaded <see cref="IFormFile"/> for the specified entity type <typeparamref name="T"/>.
    /// The file content is preserved in memory and a populated <see cref="IFileEntry"/> is returned.
    /// The returned entry's <see cref="IFileEntry.FileLocation"/> is a provider-relative path (URL-safe).
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">Uploaded form file to store (required).</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the stored <see cref="IFileEntry"/>.</returns>
    public async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Category is required.", nameof(category));

        await SimulateDelayAsync(cancellationToken).ConfigureAwait(false);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var entityName = typeof(T).Name;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            entityName = entityName.Replace(@"\", "/", StringComparison.OrdinalIgnoreCase);

        var typeFolder = ImageProcessingHelper.GetFileTypeFolder(extension);
        var relativeFolderPath = Path.Combine(category, entityName, typeFolder);

        var fileEntry = new FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(file.FileName),
            Size = file.Length,
            ContentType = file.ContentType,
            UploadedTime = DateTime.UtcNow,
            FileExtension = extension,
            Encrypted = false,
            Category = category,
            Metadata = new Dictionary<string, string>()
        };

        // Sanitize original file name and build provider-relative location similar to LocalFileStorageManager
        var originalName = Path.GetFileNameWithoutExtension(file.FileName) ?? string.Empty;
        originalName = string.Concat(originalName.Split(Path.GetInvalidFileNameChars()));
        var fileName = $"{fileEntry.Id:N}-{originalName}{extension}";
        fileEntry.FileLocation = Path.Combine(relativeFolderPath, fileName).Replace('\\', '/');

        // Read file into memory
        await using var inStream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await inStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();

        var stored = new FakeStoredFile(fileEntry, bytes);
        _store[fileEntry.FileLocation] = stored;

        return fileEntry;
    }

    /// <summary>
    /// Create and store an uploaded file and associate it with the provided <paramref name="entity"/>.
    /// If the entity implements <see cref="IFileAttachable"/>, this method will call
    /// <see cref="IFileAttachable.AddFileAttachment"/> to attach the created file entry.
    /// </summary>
    /// <typeparam name="T">Entity type which implements <see cref="IFileAttachable"/>.</typeparam>
    /// <param name="entity">Entity instance to attach the file entry to (required).</param>
    /// <param name="file">Uploaded form file to store (required).</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created primary <see cref="IFileEntry"/>.</returns>
    public async Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken)
        where T : class, IFileAttachable
    {
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);

        entity.AddFileAttachment(primary);
        return primary;
    }

    /// <summary>
    /// Create the primary/original file and in-memory image variants (if the uploaded file is an image).
    /// Returns the primary <see cref="IFileEntry"/> and a list of created variant entries. Variants are
    /// stored in the in-memory fake store so tests can inspect them via <see cref="ListFiles"/> or
    /// <see cref="TryGetFile"/>.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">Uploaded form file to store (required).</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to a tuple containing the primary entry and created variants.</returns>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        // Reuse primary creation logic
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);

        var variants = new List<IFileEntry>();

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!ImageProcessingHelper.IsImageExtension(extension))
            return (primary, variants);

        // Read bytes once
        await using var stream = file.OpenReadStream();
        using var m = new MemoryStream();
        await stream.CopyToAsync(m, cancellationToken).ConfigureAwait(false);
        var bytes = m.ToArray();

        // Compressed variant (use Local defaults)
        var compressedMaxW = 1200;
        var compressedMaxH = 1200;
        var jpegQuality = 85;

        // Create compressed
        using (var src = new MemoryStream(bytes))
        {
            await using var outStream = new MemoryStream();
            await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream, compressedMaxW, compressedMaxH, jpegQuality, cancellationToken).ConfigureAwait(false);
            var compBytes = outStream.ToArray();

            var relativeDir = Path.GetDirectoryName(primary.FileLocation) ?? string.Empty;
            var compressedRelative = Path.Combine(relativeDir, "compressed", $"{primary.Id:N}.jpg").Replace('\\', '/');

            var compEntry = BuildVariantFileEntryInMemory(compressedRelative, compBytes);
            compEntry.VariantType = "compressed";
            compEntry.Category = category;
            compEntry.Encrypted = false;
            variants.Add(compEntry);

            // store
            _store[compEntry.FileLocation] = new FakeStoredFile(compEntry, compBytes);

            primary.Metadata ??= new Dictionary<string, string>();
            primary.Metadata["CompressedPath"] = compressedRelative;
        }

        // Thumbnail
        var thumbMaxW = 200;
        var thumbMaxH = 200;
        using (var src = new MemoryStream(bytes))
        {
            await using var outStream = new MemoryStream();
            await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream, thumbMaxW, thumbMaxH, jpegQuality, cancellationToken).ConfigureAwait(false);
            var thumbBytes = outStream.ToArray();

            var relativeDir = Path.GetDirectoryName(primary.FileLocation) ?? string.Empty;
            var thumbRelative = Path.Combine(relativeDir, "thumbnails", $"{primary.Id:N}.jpg").Replace('\\', '/');
            var thumbEntry = BuildVariantFileEntryInMemory(thumbRelative, thumbBytes);
            thumbEntry.VariantType = "thumbnail";
            thumbEntry.Category = category;
            thumbEntry.Encrypted = false;

            variants.Add(thumbEntry);

            _store[thumbEntry.FileLocation] = new FakeStoredFile(thumbEntry, thumbBytes);

            primary.Metadata ??= new Dictionary<string, string>();
            primary.Metadata["ThumbnailPath"] = thumbRelative;
        }

        return (primary, variants);
    }

    /// <summary>
    /// Build a simple <see cref="FileEntry"/> describing an in-memory variant file.
    /// The <paramref name="relativePath"/> should be a provider-relative path (URL-safe).
    /// </summary>
    /// <param name="relativePath">Provider-relative path for the variant.</param>
    /// <param name="content">Byte content of the variant (used to set Size).</param>
    /// <returns>A new <see cref="FileEntry"/> instance for the variant.</returns>
    private static FileEntry BuildVariantFileEntryInMemory(string relativePath, byte[] content)
    {
        // Derive VariantType from the containing folder name
        var dir = Path.GetDirectoryName(relativePath) ?? string.Empty;
        var variantType = Path.GetFileName(dir) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(variantType)) variantType = null;

        var entry = new FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(relativePath),
            FileLocation = relativePath,
            Size = content?.LongLength ?? 0,
            ContentType = "image/jpeg",
            UploadedTime = DateTime.UtcNow,
            VariantType = variantType,
        };

        return entry;
    }

    /// <summary>
    /// Simulate a small operation delay when configured via <see cref="FakeOptions.OperationDelayMs"/>.
    /// This helps tests exercise timeouts and concurrency scenarios without hitting real IO.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SimulateDelayAsync(CancellationToken cancellationToken)
    {
        if (_options.OperationDelayMs > 0)
            await Task.Delay(_options.OperationDelayMs, cancellationToken).ConfigureAwait(false);
    }

    // IFakeFileStore
    /// <summary>
    /// Return a read-only snapshot of files currently stored in the fake in-memory store.
    /// Useful for assertions in unit tests.
    /// </summary>
    public IReadOnlyCollection<FakeStoredFile> ListFiles() => _store.Values.ToList().AsReadOnly();

    /// <summary>
    /// Attempt to retrieve a stored file by its provider-relative location.
    /// </summary>
    /// <param name="fileLocation">Provider-relative file location to lookup.</param>
    /// <param name="file">Out parameter set to the stored file when found; otherwise null.</param>
    /// <returns>True if the file was found, false otherwise.</returns>
    public bool TryGetFile(string fileLocation, out FakeStoredFile? file) => _store.TryGetValue(fileLocation, out file);

    /// <summary>
    /// Clear all stored files from the in-memory fake store. Useful for test teardown.
    /// </summary>
    public void Clear() => _store.Clear();
}
