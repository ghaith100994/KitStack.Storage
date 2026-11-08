using System.Security.Cryptography;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.IO;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Utilities;
using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;

namespace KitStack.Abstractions.Services;

/// <summary>
/// Abstract base class for file storage managers.
/// It owns everything that is the same for every provider: input validation, the upload policy,
/// content-type resolution, SHA-256 checksums, encryption at rest, the storage layout and the
/// image-variant pipeline. A provider only implements the primitive operations
/// (<see cref="WriteCoreAsync"/>, <see cref="OpenReadCoreAsync"/>, <see cref="ExistsCoreAsync"/>,
/// <see cref="DeleteCoreAsync"/> and optionally <see cref="GetUrlCoreAsync"/>).
/// </summary>
public abstract class FileStorageManagerBase : IFileStorageManager, IStorageContentAccess
{
    /// <summary>Variant type of an original (non-variant) file.</summary>
    public const string OriginalVariant = "original";

    /// <summary>Create the base with the shared storage options and an optional encryptor.</summary>
    /// <param name="storageOptions">Shared options (upload policy, encryption key). <c>null</c> uses defaults.</param>
    /// <param name="encryptor">
    /// Content encryptor. When <c>null</c>, an <see cref="AesGcmFileContentEncryptor"/> is created if
    /// <see cref="StorageOptions.MasterEncryptionKey"/> is set; otherwise content is stored in plaintext.
    /// </param>
    protected FileStorageManagerBase(StorageOptions? storageOptions = null, IFileContentEncryptor? encryptor = null)
    {
        StorageOptions = storageOptions ?? new StorageOptions();
        Encryptor = encryptor ?? AesGcmFileContentEncryptor.FromOptions(StorageOptions);
        StorageName = StorageInstance.Normalize(StorageOptions.InstanceName);

        // An instance that writes plaintext can still read files encrypted earlier with one of its previous keys.
        _decryptor = Encryptor ?? (StorageOptions.PreviousEncryptionKeys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k)) is { } first
            ? new AesGcmFileContentEncryptor(first, StorageOptions.PreviousEncryptionKeys.Where(k => k != first))
            : null);
    }

    private readonly IFileContentEncryptor? _decryptor;

    /// <summary>
    /// Name of the storage instance this manager serves (<see cref="StorageOptions.InstanceName"/>, or <c>default</c>).
    /// Written into <see cref="IFileEntry.StorageName"/> of every entry it creates.
    /// </summary>
    public string StorageName { get; }

    /// <summary>Shared storage options.</summary>
    protected StorageOptions StorageOptions { get; }

    /// <summary>The active encryptor, or <c>null</c> when content is stored in plaintext.</summary>
    protected IFileContentEncryptor? Encryptor { get; }

    /// <summary>Provider identifier written into every <see cref="FileEntry.StorageProvider"/>.</summary>
    protected abstract string StorageProvider { get; }

    /// <summary>
    /// Returns the active image-processing configuration, or <c>null</c> to skip variant generation.
    /// </summary>
    protected abstract ImageProcessingOptions? GetImageProcessingOptions();

    // ── Provider primitives ─────────────────────────────────────────────────

    /// <summary>
    /// Store <see cref="StorageWriteRequest.Content"/> and return the provider location
    /// (relative path for Local/FTP/SFTP, object key for S3, blob name for Azure).
    /// </summary>
    protected abstract Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken);

    /// <summary>Open the stored bytes for reading, or return <c>null</c> when they do not exist.</summary>
    protected abstract Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken);

    /// <summary>True when the stored bytes exist.</summary>
    protected abstract Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken);

    /// <summary>Delete the stored bytes. Must not throw when they do not exist.</summary>
    protected abstract Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken);

    /// <summary>Direct download URL, or <c>null</c> (default) when the provider cannot produce one.</summary>
    protected virtual Task<Uri?> GetUrlCoreAsync(StorageLocation location, TimeSpan? lifetime, CancellationToken cancellationToken)
        => Task.FromResult<Uri?>(null);

    /// <summary>
    /// Replace the bytes stored at an existing location. Providers that support
    /// <see cref="OverwriteAsync"/> override this; the default throws <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual Task OverwriteCoreAsync(StorageLocation location, Stream content, long length, string contentType, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{GetType().Name} does not support overwriting files in place.");

    /// <summary>
    /// The location <see cref="WriteCoreAsync"/> returns for a folder, file name and variant type, or <c>null</c> (default)
    /// when it cannot be predicted. Enables resumable transfers (<see cref="IStorageContentAccess.PredictLocation"/>).
    /// </summary>
    protected virtual string? GetWriteLocation(string folder, string fileName, string variantType) => null;

    /// <summary>Value for <see cref="IFileEntry.StoragePath"/>; <c>null</c> (default) when not applicable.</summary>
    protected virtual string? GetStoragePath(string location) => null;

    /// <summary>Called once the entry for a stored original or variant is complete. Default: no-op.</summary>
    protected virtual void OnEntryCreated(IFileEntry entry)
    {
    }

    // ── IFileStorageManager: writes ─────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(file);
        return CreateFromFormFileAsync<T>(file, category, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IFileEntry> CreateAsync<T>(
        Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default)
        where T : class
        => CreateAsync(GenericOptions<T>(fileName, contentType, category), content, cancellationToken);

    /// <inheritdoc/>
    public async Task<IFileEntry> CreateAsync<T>(
        T entity, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class, IFileAttachable
    {
        ArgumentNullException.ThrowIfNull(entity);
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);
        entity.AddFileAttachment(primary);
        primary.LinkToEntity(entity, category ?? typeof(T).Name);
        return primary;
    }

    /// <inheritdoc/>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(
        IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(file);
        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
            return await CreateWithVariantsAsync<T>(stream, file.FileName, file.ContentType, category, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(
        Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default)
        where T : class
        => CreateWithVariantsAsync(GenericOptions<T>(fileName, contentType, category), content, cancellationToken);

    /// <inheritdoc/>
    public async Task<IFileEntry> CreateAsync(StorageWriteOptions options, Stream content, CancellationToken cancellationToken = default)
    {
        var stored = await StoreOriginalAsync(options, content, cancellationToken).ConfigureAwait(false);
        await using (stored.Content.ConfigureAwait(false))
            return stored.Entry;
    }

    /// <inheritdoc/>
    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync(
        StorageWriteOptions options, Stream content, CancellationToken cancellationToken = default)
    {
        var stored = await StoreOriginalAsync(options, content, cancellationToken).ConfigureAwait(false);
        await using (stored.Content.ConfigureAwait(false))
        {
            var imageOptions = GetImageProcessingOptions();
            var primary = stored.Entry;
            if (imageOptions is null || !CanCreateVariants(primary))
                return (primary, []);

            var variants = await ProcessVariantsAsync(primary, stored.Folder, stored.Content, options.Category, imageOptions, cancellationToken)
                .ConfigureAwait(false);
            return (primary, variants);
        }
    }

    private static StorageWriteOptions GenericOptions<T>(string fileName, string? contentType, string? category)
        => new()
        {
            FileName = fileName,
            DeclaredContentType = contentType,
            Category = category,
            EntityName = typeof(T).Name,
        };

    // ── IFileStorageManager: reads, deletes, URLs ───────────────────────────

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        var location = ToLocation(entry);
        if (entry.Encrypted && _decryptor is null)
            throw new StorageConfigurationException($"File '{entry.FileLocation}' is encrypted but no encryption key is configured.");

        var stream = await OpenReadCoreAsync(location, cancellationToken).ConfigureAwait(false)
            ?? throw new StorageNotFoundException($"File '{entry.FileLocation}' was not found.");

        entry.LastAccessedTime = DateTimeOffset.UtcNow;
        return _decryptor is null
            ? stream
            : await DecryptIfNeededAsync(stream, entry, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(IFileEntry entry, CancellationToken cancellationToken = default)
        => ExistsCoreAsync(ToLocation(entry), cancellationToken);

    /// <inheritdoc/>
    public async Task DeleteAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        var location = ToLocation(entry);
        await DeleteCoreAsync(location, cancellationToken).ConfigureAwait(false);

        if (location.VariantType == OriginalVariant && entry.Metadata is { Count: > 0 } metadata)
        {
            foreach (var variant in GetVariantLocations(metadata))
                await DeleteCoreAsync(variant, cancellationToken).ConfigureAwait(false);
        }

        entry.IsDeleted = true;
    }

    /// <inheritdoc/>
    public Task<Uri?> GetUrlAsync(IFileEntry entry, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        var location = ToLocation(entry);
        if (lifetime is { } l && l <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "URL lifetime must be positive.");

        // A direct URL would hand out ciphertext; encrypted files must go through OpenReadAsync.
        return entry.Encrypted
            ? Task.FromResult<Uri?>(null)
            : GetUrlCoreAsync(location, lifetime, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task OverwriteAsync(IFileEntry entry, Stream content, CancellationToken cancellationToken = default)
    {
        var location = ToLocation(entry);
        ArgumentNullException.ThrowIfNull(content);

        var policy = StorageOptions.Upload ?? new UploadPolicy();
        var extension = string.IsNullOrWhiteSpace(entry.FileExtension) ? StoragePaths.GetExtension(entry.FileLocation) : entry.FileExtension;
        var spool = await SpooledContent.CreateAsync(content, policy.MaxBytes, cancellationToken).ConfigureAwait(false);
        await using (spool.ConfigureAwait(false))
        {
            var contentType = location.VariantType == OriginalVariant
                ? ContentTypeResolver.Resolve(spool.Header, extension)
                : entry.ContentType;
            if (location.VariantType == OriginalVariant && !policy.IsContentTypeAllowed(contentType))
                throw new StorageValidationException($"Content type '{contentType}' is not allowed.");

            await InStoredFormAsync(spool.Rewind(), spool.Length, contentType, async (stored, storedLength, storedType) =>
            {
                await OverwriteCoreAsync(location, stored, storedLength, storedType, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            entry.Size = spool.Length;
            entry.Checksum = spool.Checksum;
            entry.ContentType = contentType;
            entry.Encrypted = Encryptor is not null;
        }
    }

    // ── Write pipeline ──────────────────────────────────────────────────────

    private async Task<IFileEntry> CreateFromFormFileAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken)
        where T : class
    {
        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
            return await CreateAsync<T>(stream, file.FileName, file.ContentType, category, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredOriginal> StoreOriginalAsync(StorageWriteOptions options, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);
        var fileName = options.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
            throw new StorageValidationException("File name is required.");

        var extension = StoragePaths.GetExtension(fileName);
        string folder;
        if (!string.IsNullOrWhiteSpace(options.Folder))
        {
            folder = StoragePaths.SanitizeFolder(options.Folder);
        }
        else
        {
            var categoryPath = StoragePaths.SanitizeCategory(options.Category ?? string.Empty);
            if (string.IsNullOrWhiteSpace(options.EntityName))
                throw new StorageValidationException("EntityName is required when no Folder is given.");
            folder = StoragePaths.Combine(
                categoryPath,
                StoragePaths.SanitizeSegment(options.EntityName),
                ImageProcessingHelper.GetFileTypeFolder(extension));
        }

        var policy = new EffectivePolicy(StorageOptions.Upload ?? new UploadPolicy(), options.Policy);
        if (!policy.IsExtensionAllowed(extension))
            throw new StorageValidationException($"File extension '{extension}' is not allowed.");

        var spool = await SpooledContent.CreateAsync(content, policy.MaxBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var contentType = ContentTypeResolver.Resolve(spool.Header, extension);
            if (!policy.IsContentTypeAllowed(contentType))
                throw new StorageValidationException($"Content type '{contentType}' is not allowed.");

            var id = Guid.CreateVersion7();
            var displayName = Path.GetFileName(fileName.Replace('\\', '/'));
            var storedName = $"{id:N}-{StoragePaths.SanitizeSegment(Path.GetFileNameWithoutExtension(displayName))}{extension}";

            var entry = new FileEntry
            {
                Id = id,
                FileName = displayName,
                OriginalFileName = fileName,
                Size = spool.Length,
                ContentType = contentType,
                Checksum = spool.Checksum,
                UploadedTime = DateTimeOffset.UtcNow,
                FileExtension = extension,
                Category = options.Category,
                Encrypted = Encryptor is not null,
                StorageProvider = StorageProvider,
                StorageName = StorageName,
                LastAccessedTime = DateTimeOffset.UtcNow,
                VariantType = OriginalVariant,
                Metadata = options.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(options.Metadata),
            };
            if (!string.IsNullOrWhiteSpace(options.DeclaredContentType))
                entry.Metadata[FileEntryMetadataKeys.DeclaredContentType] = options.DeclaredContentType;
            entry.Metadata[FileEntryMetadataKeys.Folder] = folder;
            if (options.Link is { } link)
            {
                if (string.IsNullOrWhiteSpace(link.EntityName) || string.IsNullOrWhiteSpace(link.EntityId))
                    throw new StorageValidationException("Link requires EntityName and EntityId.");
                entry.RelatedEntities!.Add(new FileRelatedEntity
                {
                    EntityName = link.EntityName,
                    EntityId = link.EntityId,
                    Notes = link.Notes,
                    IsUsed = true,
                    LinkedOn = DateTimeOffset.UtcNow,
                });
            }

            entry.FileLocation = await WriteAsync(
                folder, storedName, spool.Rewind(), spool.Length, contentType, OriginalVariant, cancellationToken).ConfigureAwait(false);
            entry.StoragePath = GetStoragePath(entry.FileLocation);
            OnEntryCreated(entry);

            return new StoredOriginal(entry, folder, spool);
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ── IStorageContentAccess ───────────────────────────────────────────────

    /// <inheritdoc/>
    public string? PredictLocation(string folder, string fileName, string variantType)
        => GetWriteLocation(StoragePaths.SanitizeFolder(folder), StoragePaths.SanitizeSegment(fileName), variantType);

    /// <inheritdoc/>
    public async Task<StoredContent> PutContentAsync(StorageContentWrite request, Stream plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plaintext);
        var folder = StoragePaths.SanitizeFolder(request.Folder);
        var fileName = StoragePaths.SanitizeSegment(request.FileName);
        var spool = await SpooledContent.CreateAsync(plaintext, 0, cancellationToken).ConfigureAwait(false);
        await using (spool.ConfigureAwait(false))
        {
            var variantType = string.IsNullOrWhiteSpace(request.VariantType) ? OriginalVariant : request.VariantType;
            var location = await WriteAsync(folder, fileName, spool.Rewind(), spool.Length, request.ContentType, variantType, cancellationToken)
                .ConfigureAwait(false);
            return new StoredContent(location, GetStoragePath(location), spool.Length, spool.Checksum, Encryptor is not null, StorageProvider);
        }
    }

    /// <inheritdoc/>
    public async Task<StoredContent?> VerifyContentAsync(string location, string variantType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location))
            throw new StorageValidationException("Location is required.");
        var raw = await OpenReadCoreAsync(new StorageLocation(location, string.IsNullOrWhiteSpace(variantType) ? OriginalVariant : variantType), cancellationToken)
            .ConfigureAwait(false);
        if (raw is null)
            return null;

        var (plaintext, encrypted) = _decryptor is null
            ? (raw, false)
            : await DecryptCoreAsync(raw, new FileEntry { FileLocation = location }, cancellationToken).ConfigureAwait(false);
        await using (plaintext.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await plaintext.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                size += read;
            }

            return new StoredContent(location, GetStoragePath(location), size, Convert.ToHexStringLower(hash.GetHashAndReset()), encrypted, StorageProvider);
        }
    }

    /// <inheritdoc/>
    public Task DeleteContentAsync(string location, string variantType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location))
            throw new StorageValidationException("Location is required.");
        return DeleteCoreAsync(new StorageLocation(location, string.IsNullOrWhiteSpace(variantType) ? OriginalVariant : variantType), cancellationToken);
    }

    private Task<string> WriteAsync(
        string folder, string fileName, Stream plaintext, long length, string contentType, string variantType, CancellationToken cancellationToken)
        => InStoredFormAsync(plaintext, length, contentType, (content, storedLength, storedType) =>
            WriteCoreAsync(new StorageWriteRequest(folder, fileName, content, storedLength, storedType, variantType), cancellationToken), cancellationToken);

    /// <summary>
    /// Hand <paramref name="write"/> the bytes as they are stored: the plaintext, or its encrypted form when
    /// encryption is on (labelled <c>application/octet-stream</c>, since ciphertext is never the plaintext type).
    /// </summary>
    private async Task<T> InStoredFormAsync<T>(
        Stream plaintext, long length, string contentType, Func<Stream, long, string, Task<T>> write, CancellationToken cancellationToken)
    {
        if (Encryptor is null)
            return await write(plaintext, length, contentType).ConfigureAwait(false);

        var encrypted = length < SpooledContent.MemoryThreshold ? new MemoryStream() : SpooledContent.CreateTempFile();
        await using (encrypted.ConfigureAwait(false))
        {
            await Encryptor.EncryptAsync(plaintext, encrypted, cancellationToken).ConfigureAwait(false);
            encrypted.Seek(0, SeekOrigin.Begin);
            return await write(encrypted, encrypted.Length, ContentTypes.OctetStream).ConfigureAwait(false);
        }
    }

    private async Task<Stream> DecryptIfNeededAsync(Stream stream, IFileEntry entry, CancellationToken cancellationToken)
        => (await DecryptCoreAsync(stream, entry, cancellationToken).ConfigureAwait(false)).Plaintext;

    private async Task<(Stream Plaintext, bool Encrypted)> DecryptCoreAsync(Stream stream, IFileEntry entry, CancellationToken cancellationToken)
    {
        var encryptor = _decryptor!;
        Stream source = stream;
        bool encrypted;

        // Detect by header as well as by flag, so files are read correctly even when the caller
        // rebuilt the entry without the Encrypted flag (for example from a URL path).
        var header = new byte[encryptor.HeaderLength];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        encrypted = encryptor.IsEncrypted(header.AsSpan(0, read));

        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
        else
            source = new PrefixedReadStream(header[..read], stream);

        if (!encrypted)
        {
            if (entry.Encrypted)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                throw new StorageException($"File '{entry.FileLocation}' is flagged as encrypted but its content is not.");
            }

            return (source, false);
        }

        await using (source.ConfigureAwait(false))
        {
            var plaintext = entry.Size is > 0 and < SpooledContent.MemoryThreshold
                ? new MemoryStream((int)entry.Size)
                : SpooledContent.CreateTempFile();
            try
            {
                await encryptor.DecryptAsync(source, plaintext, cancellationToken).ConfigureAwait(false);
                plaintext.Seek(0, SeekOrigin.Begin);
                return (plaintext, true);
            }
            catch
            {
                await plaintext.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    // ── Shared variant pipeline ─────────────────────────────────────────────

    private static bool CanCreateVariants(IFileEntry primary)
        => ImageProcessingHelper.IsImageExtension(primary.FileExtension ?? string.Empty)
           && primary.ContentType.StartsWith("image/", StringComparison.Ordinal);

    private async Task<List<IFileEntry>> ProcessVariantsAsync(
        IFileEntry primary,
        string folder,
        SpooledContent original,
        string? category,
        ImageProcessingOptions options,
        CancellationToken ct)
    {
        var variants = new List<IFileEntry>();
        var fileName = $"{primary.Id:N}.jpg";

        try
        {
            if (options.CreateCompressed)
            {
                var entry = await CreateVariantAsync(primary, folder, "compressed", fileName, original,
                    options.CompressedMaxWidth, options.CompressedMaxHeight, options.JpegQuality, "compressed", category, ct).ConfigureAwait(false);
                variants.Add(entry);
                (primary.Metadata ??= new Dictionary<string, string>())["CompressedPath"] = entry.FileLocation;
            }

            if (options.CreateThumbnail)
            {
                var entry = await CreateVariantAsync(primary, folder, "thumbnails", fileName, original,
                    options.ThumbnailMaxWidth, options.ThumbnailMaxHeight, options.JpegQuality, "thumbnail", category, ct).ConfigureAwait(false);
                variants.Add(entry);
                (primary.Metadata ??= new Dictionary<string, string>())["ThumbnailPath"] = entry.FileLocation;
            }

            var sizePaths = new List<string>();
            foreach (var size in options.AdditionalSizes ?? [])
            {
                if (string.IsNullOrWhiteSpace(size.SizeName))
                    continue;

                var sizeFolder = StoragePaths.SanitizeSegment(size.SizeName);
                var entry = await CreateVariantAsync(primary, folder, sizeFolder, fileName, original,
                    size.MaxWidth, size.MaxHeight, size.JpegQuality, size.SizeName, category, ct).ConfigureAwait(false);
                variants.Add(entry);
                sizePaths.Add(entry.FileLocation);
            }

            if (sizePaths.Count > 0)
                (primary.Metadata ??= new Dictionary<string, string>())["Variants"] = string.Join(';', sizePaths);
        }
        catch (ImageFormatException)
        {
            // The bytes look like an image but cannot be decoded (or the format is not supported):
            // keep the original and the variants already made, skip the rest.
        }

        return variants;
    }

    private async Task<IFileEntry> CreateVariantAsync(
        IFileEntry primary, string folder, string variantFolder, string fileName, SpooledContent original,
        int maxWidth, int maxHeight, int jpegQuality, string variantType, string? category, CancellationToken ct)
    {
        var output = new MemoryStream();
        await using (output.ConfigureAwait(false))
        {
            await ImageProcessingHelper.CreateResizedJpegToStreamAsync(
                original.Rewind(), output, maxWidth, maxHeight, jpegQuality, ct).ConfigureAwait(false);

            var size = output.Length;
            var checksum = Convert.ToHexStringLower(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)size)));
            output.Seek(0, SeekOrigin.Begin);

            var location = await WriteAsync(
                StoragePaths.Combine(folder, variantFolder), fileName, output, size, ContentTypes.Jpeg, variantType, ct).ConfigureAwait(false);

            var entry = BuildVariantEntry(location, size, variantType, category, primary);
            entry.Checksum = checksum;
            entry.StorageName = StorageName;
            (entry.Metadata ??= new Dictionary<string, string>())[FileEntryMetadataKeys.Folder] = StoragePaths.Combine(folder, variantFolder);
            entry.Encrypted = Encryptor is not null;
            entry.StoragePath = GetStoragePath(location);
            entry.CopyRelationsFrom(primary);
            OnEntryCreated(entry);
            return entry;
        }
    }

    /// <summary>
    /// Builds a <see cref="FileEntry"/> for a generated variant.
    /// Override to set additional provider-specific properties.
    /// </summary>
    protected virtual FileEntry BuildVariantEntry(
        string location, long size, string variantType, string? category, IFileEntry primary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        return new FileEntry
        {
            Id = Guid.CreateVersion7(),
            FileName = Path.GetFileName(location),
            FileLocation = location,
            Size = size,
            ContentType = ContentTypes.Jpeg,
            FileExtension = ".jpg",
            UploadedTime = DateTimeOffset.UtcNow,
            VariantType = variantType,
            StorageProvider = StorageProvider,
            OriginalFileName = primary.FileName,
            LastAccessedTime = DateTimeOffset.UtcNow,
            Category = category,
            Metadata = new Dictionary<string, string> { ["OriginalId"] = primary.Id.ToString("N") },
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static StorageLocation ToLocation(IFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.FileLocation))
            throw new StorageValidationException("File entry has no FileLocation.");
        return new StorageLocation(entry.FileLocation, string.IsNullOrWhiteSpace(entry.VariantType) ? OriginalVariant : entry.VariantType);
    }

    private static IEnumerable<StorageLocation> GetVariantLocations(IDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("CompressedPath", out var compressed) && !string.IsNullOrWhiteSpace(compressed))
            yield return new StorageLocation(compressed, "compressed");
        if (metadata.TryGetValue("ThumbnailPath", out var thumbnail) && !string.IsNullOrWhiteSpace(thumbnail))
            yield return new StorageLocation(thumbnail, "thumbnail");
        if (metadata.TryGetValue("Variants", out var sizes) && !string.IsNullOrWhiteSpace(sizes))
        {
            foreach (var path in sizes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Additional sizes live in a folder named after the size: ".../{sizeName}/{id}.jpg".
                var sizeName = Path.GetFileName(Path.GetDirectoryName(path.Replace('\\', '/')) ?? string.Empty);
                yield return new StorageLocation(path, string.IsNullOrEmpty(sizeName) ? OriginalVariant : sizeName);
            }
        }
    }

    private sealed record StoredOriginal(IFileEntry Entry, string Folder, SpooledContent Content);

    /// <summary>The instance policy intersected with an optional per-call policy: a file must satisfy both.</summary>
    private sealed class EffectivePolicy(UploadPolicy instance, UploadPolicy? call)
    {
        public long MaxBytes => (instance.MaxBytes, call?.MaxBytes ?? 0) switch
        {
            (> 0 and var a, > 0 and var b) => Math.Min(a, b),
            (> 0 and var a, _) => a,
            (_, var b) => b,
        };

        public bool IsExtensionAllowed(string extension)
            => instance.IsExtensionAllowed(extension) && (call?.IsExtensionAllowed(extension) ?? true);

        public bool IsContentTypeAllowed(string contentType)
            => instance.IsContentTypeAllowed(contentType) && (call?.IsContentTypeAllowed(contentType) ?? true);
    }
}

/// <summary>Content handed to <see cref="FileStorageManagerBase"/> providers for storage.</summary>
/// <param name="Folder">Logical folder, for example <c>Invoices/Order/Others</c> or <c>Users/User/Images/thumbnails</c>. Providers add their own base path or prefix.</param>
/// <param name="FileName">Sanitized file name to store under <paramref name="Folder"/>.</param>
/// <param name="Content">Content to store, positioned at 0. Not owned by the provider.</param>
/// <param name="Length">Content length in bytes.</param>
/// <param name="ContentType">Content type of the stored bytes.</param>
/// <param name="VariantType"><c>original</c>, <c>compressed</c>, <c>thumbnail</c> or an additional size name; providers may route on it.</param>
public sealed record StorageWriteRequest(string Folder, string FileName, Stream Content, long Length, string ContentType, string VariantType);

/// <summary>A stored file as seen by a provider.</summary>
/// <param name="Location">Provider location returned by <c>WriteCoreAsync</c>.</param>
/// <param name="VariantType">Variant type used to pick the right target when providers route variants.</param>
public readonly record struct StorageLocation(string Location, string VariantType);
