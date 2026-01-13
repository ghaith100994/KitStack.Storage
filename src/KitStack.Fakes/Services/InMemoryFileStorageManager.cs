using System.Collections.Concurrent;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Utilities;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
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

    // High-level convenience to create/store an IFormFile for an entity T (in-memory)
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

            variants.Add(thumbEntry);

            _store[thumbEntry.FileLocation] = new FakeStoredFile(thumbEntry, thumbBytes);

            primary.Metadata ??= new Dictionary<string, string>();
            primary.Metadata["ThumbnailPath"] = thumbRelative;
        }

        return (primary, variants);
    }

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

    private async Task SimulateDelayAsync(CancellationToken cancellationToken)
    {
        if (_options.OperationDelayMs > 0)
            await Task.Delay(_options.OperationDelayMs, cancellationToken).ConfigureAwait(false);
    }

    // IFakeFileStore
    public IReadOnlyCollection<FakeStoredFile> ListFiles() => _store.Values.ToList().AsReadOnly();

    public bool TryGetFile(string fileLocation, out FakeStoredFile? file) => _store.TryGetValue(fileLocation, out file);

    public void Clear() => _store.Clear();
}
