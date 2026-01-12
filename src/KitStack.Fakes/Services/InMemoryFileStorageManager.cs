using System.Collections.Concurrent;
using KitStack.Abstractions.Interfaces;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Models;
using KitStack.Fakes.Options;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using KitStack.Abstractions.Models;

namespace KitStack.Fakes.Services;

/// <summary>
/// In-memory implementation of IFileStorageManager used for unit tests and local development.
/// Also implements IFakeFileStore so tests can inspect the stored state.
/// </summary>
public class InMemoryFileStorageManager : IFileStorageManager, IFakeFileStore
{
    private readonly ConcurrentDictionary<string, FakeStoredFile> _store = new();
    private readonly FakeOptions _options;

    public InMemoryFileStorageManager(IOptions<FakeOptions>? options = null)
    {
        _options = options?.Value ?? new FakeOptions();
    }

    // High-level convenience to create/store an IFormFile for an entity T (in-memory)
    public async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Category is required.", nameof(category));

        await SimulateDelayAsync(cancellationToken).ConfigureAwait(false);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var entityName = typeof(T).Name;

        var typeFolder = KitStack.Abstractions.Utilities.ImageProcessingHelper.GetFileTypeFolder(extension);
        var relativeFolderPath = Path.Combine(category, entityName, typeFolder);

        var fileEntry = new KitStack.Abstractions.Models.FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(file.FileName),
            Size = file.Length,
            ContentType = file.ContentType,
            UploadedTime = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>()
        };

        fileEntry.Metadata[StorageMetadataKeys.FileExtension] = extension;

        // Build a provider-relative location
        var fileName = $"{fileEntry.Id:N}{extension}";
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

    private async Task SimulateDelayAsync(CancellationToken cancellationToken)
    {
        if (_options.OperationDelayMs > 0)
            await Task.Delay(_options.OperationDelayMs, cancellationToken);
    }

    public async Task CreateAsync(IFileEntry fileEntry, Stream content, CancellationToken cancellationToken = default)
    {
        if (fileEntry == null) throw new ArgumentNullException(nameof(fileEntry));
        if (content == null) throw new ArgumentNullException(nameof(content));

        await SimulateDelayAsync(cancellationToken);

        // read to memory
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();

        if (_options.MaxFileSizeBytes.HasValue && bytes.LongLength > _options.MaxFileSizeBytes.Value)
            throw new InvalidOperationException("File too large for fake store.");

        var stored = new FakeStoredFile(fileEntry, bytes);
        _store[fileEntry.FileLocation] = stored;
    }

    public async Task<byte[]> ReadAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        if (fileEntry == null) throw new ArgumentNullException(nameof(fileEntry));
        await SimulateDelayAsync(cancellationToken);

        if (!_store.TryGetValue(fileEntry.FileLocation, out var stored))
            throw new IOException("File not found in fake store.");

        // return copy
        var copy = new byte[stored.Content.Length];
        Buffer.BlockCopy(stored.Content, 0, copy, 0, stored.Content.Length);
        return copy;
    }

    public async Task<Stream> ReadAsStreamAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAsync(fileEntry, cancellationToken);
        return new MemoryStream(bytes, writable: false);
    }

    public Task DeleteAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        if (fileEntry == null) throw new ArgumentNullException(nameof(fileEntry));
        _store.TryRemove(fileEntry.FileLocation, out _);
        return Task.CompletedTask;
    }

    public Task ArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        if (fileEntry == null) throw new ArgumentNullException(nameof(fileEntry));
        if (_store.TryGetValue(fileEntry.FileLocation, out var stored))
        {
            stored.IsArchived = true;
        }
        return Task.CompletedTask;
    }

    public Task UnArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        if (fileEntry == null) throw new ArgumentNullException(nameof(fileEntry));
        if (_store.TryGetValue(fileEntry.FileLocation, out var stored))
        {
            stored.IsArchived = false;
        }
        return Task.CompletedTask;
    }

    // IFakeFileStore
    public IReadOnlyCollection<FakeStoredFile> ListFiles() => _store.Values.ToList().AsReadOnly();

    public bool TryGetFile(string fileLocation, out FakeStoredFile? file) => _store.TryGetValue(fileLocation, out file);

    public void Clear() => _store.Clear();
}