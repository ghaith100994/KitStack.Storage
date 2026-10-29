using System.Collections.Concurrent;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Models;
using KitStack.Fakes.Options;
using Microsoft.Extensions.Options;

namespace KitStack.Fakes.Services;

/// <summary>
/// In-memory implementation of IFileStorageManager used for unit tests and local development.
/// It runs the same pipeline as the real providers (upload policy, content-type resolution, checksums,
/// encryption, variants) and implements IFakeFileStore so tests can inspect the stored state.
/// </summary>
public class InMemoryFileStorageManager : FileStorageManagerBase, IFakeFileStore
{
    private readonly ConcurrentDictionary<string, FakeStoredFile> _store = new();
    private readonly FakeOptions _options;

    public InMemoryFileStorageManager(
        IOptions<FakeOptions>? options = null,
        IOptions<StorageOptions>? storageOptions = null,
        IFileContentEncryptor? encryptor = null)
        : base(WithFakeLimit(storageOptions?.Value, options?.Value), encryptor)
    {
        _options = options?.Value ?? new FakeOptions();
    }

    protected override string StorageProvider => "Fake";

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _options.ImageProcessing;

    protected override async Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SimulateDelayAsync(cancellationToken).ConfigureAwait(false);

        using var ms = new MemoryStream();
        await request.Content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        var location = $"{request.Folder}/{request.FileName}";
        // Placeholder entry; replaced by the complete one in OnEntryCreated.
        var placeholder = new Abstractions.Models.FileEntry
        {
            FileLocation = location,
            FileName = request.FileName,
            Size = request.Length,
            ContentType = request.ContentType,
            VariantType = request.VariantType,
            StorageProvider = StorageProvider,
        };
        _store[location] = new FakeStoredFile(placeholder, ms.ToArray());
        return location;
    }

    protected override async Task OverwriteCoreAsync(StorageLocation location, Stream content, long length, string contentType, CancellationToken cancellationToken)
    {
        await SimulateDelayAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        var entry = _store.TryGetValue(location.Location, out var existing)
            ? existing.FileEntry
            : new Abstractions.Models.FileEntry { FileLocation = location.Location, VariantType = location.VariantType, StorageProvider = StorageProvider };
        _store[location.Location] = new FakeStoredFile(entry, ms.ToArray());
    }

    protected override void OnEntryCreated(IFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_store.TryGetValue(entry.FileLocation, out var stored))
            _store[entry.FileLocation] = new FakeStoredFile(entry, stored.Content);
    }

    protected override async Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        await SimulateDelayAsync(cancellationToken).ConfigureAwait(false);
        return _store.TryGetValue(location.Location, out var file)
            ? new MemoryStream(file.Content, writable: false)
            : null;
    }

    protected override async Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        await SimulateDelayAsync(cancellationToken).ConfigureAwait(false);
        return _store.ContainsKey(location.Location);
    }

    protected override async Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        await SimulateDelayAsync(cancellationToken).ConfigureAwait(false);
        _store.TryRemove(location.Location, out _);
    }

    protected override Task<Uri?> GetUrlCoreAsync(StorageLocation location, TimeSpan? lifetime, CancellationToken cancellationToken)
        => Task.FromResult<Uri?>(new Uri($"fake://storage/{location.Location}"));

    /// <summary>
    /// Simulate a small operation delay when configured via <see cref="FakeOptions.OperationDelayMs"/>.
    /// This helps tests exercise timeouts and concurrency scenarios without hitting real IO.
    /// </summary>
    private async Task SimulateDelayAsync(CancellationToken cancellationToken)
    {
        if (_options.OperationDelayMs > 0)
            await Task.Delay(_options.OperationDelayMs, cancellationToken).ConfigureAwait(false);
    }

    private static StorageOptions? WithFakeLimit(StorageOptions? storage, FakeOptions? fake)
    {
        if (fake?.MaxFileSizeBytes is not { } limit)
            return storage;

        var copy = new StorageOptions
        {
            Provider = storage?.Provider,
            MasterEncryptionKey = storage?.MasterEncryptionKey,
            Upload = new UploadPolicy
            {
                MaxBytes = storage?.Upload.MaxBytes is > 0 and var configured ? Math.Min(configured, limit) : limit,
            },
        };
        foreach (var key in storage?.PreviousEncryptionKeys ?? [])
            copy.PreviousEncryptionKeys.Add(key);
        foreach (var type in storage?.Upload.AllowedContentTypes ?? new HashSet<string>())
            copy.Upload.AllowedContentTypes.Add(type);
        foreach (var ext in storage?.Upload.AllowedExtensions ?? new HashSet<string>())
            copy.Upload.AllowedExtensions.Add(ext);
        return copy;
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
    public bool TryGetFile(string fileLocation, out FakeStoredFile? file) => _store.TryGetValue(fileLocation, out file);

    /// <summary>
    /// Clear all stored files from the in-memory fake store. Useful for test teardown.
    /// </summary>
    public void Clear() => _store.Clear();
}
