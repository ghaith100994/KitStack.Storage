using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using KitStack.Abstractions.Utilities;
using KitStack.Storage.Azure.Options;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Azure.Services;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IFileStorageManager"/>.
/// </summary>
public sealed class AzureBlobFileStorageManager : FileStorageManagerBase
{
    private readonly AzureBlobOptions _options;
    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _containerGate = new(1, 1);
    private volatile bool _containerReady;

    protected override string StorageProvider => "Azure";

    public AzureBlobFileStorageManager(
        IOptions<AzureBlobOptions> options,
        IOptions<StorageOptions>? storageOptions = null,
        IFileContentEncryptor? encryptor = null,
        BlobServiceClient? serviceClient = null)
        : base(storageOptions?.Value, encryptor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _service = serviceClient ?? CreateServiceClient(_options);
        _container = _service.GetBlobContainerClient(_options.ContainerName);
        _containerReady = !_options.CreateContainerIfNotExists;
    }

    /// <summary>The container client, for operations this abstraction does not cover.</summary>
    public BlobContainerClient Container => _container;

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _options.ImageProcessing;

    protected override async Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var blobName = StoragePaths.Combine(_options.Prefix, request.Folder, request.FileName);
        await UploadAsync(blobName, request.Content, request.ContentType, request.VariantType, cancellationToken).ConfigureAwait(false);
        return blobName;
    }

    protected override async Task OverwriteCoreAsync(StorageLocation location, Stream content, long length, string contentType, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        await UploadAsync(location.Location, content, contentType, location.VariantType, cancellationToken).ConfigureAwait(false);
    }

    private Task UploadAsync(string blobName, Stream content, string contentType, string variantType, CancellationToken cancellationToken)
    {
        var tier = variantType == OriginalVariant
            ? ParseTier(_options.AccessTier)
            : ParseTier(_options.VariantAccessTier) ?? ParseTier(_options.AccessTier);

        // No request conditions: an existing blob is replaced.
        return _container.GetBlobClient(blobName).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            AccessTier = tier,
        }, cancellationToken);
    }

    protected override async Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.GetBlobClient(location.Location)
                .DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobArchived)
        {
            throw new StorageException($"Blob '{location.Location}' is in the Archive tier; rehydrate it with SetAccessTierAsync before reading.", ex);
        }
    }

    protected override async Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => (await _container.GetBlobClient(location.Location).ExistsAsync(cancellationToken).ConfigureAwait(false)).Value;

    protected override Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken)
        => _container.GetBlobClient(location.Location)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);

    protected override async Task<Uri?> GetUrlCoreAsync(StorageLocation location, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(location.Location);
        var expiresOn = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromSeconds(_options.SasUrlExpirationSeconds));
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
        };

        // Shared key (connection string): sign locally.
        if (blob.CanGenerateSasUri)
            return blob.GenerateSasUri(sas);

        // Token credential (managed identity, ...): sign with a user delegation key (max 7 days).
        try
        {
            var startsOn = DateTimeOffset.UtcNow.AddMinutes(-5);
            var key = await _service.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken).ConfigureAwait(false);
            sas.StartsOn = startsOn;
            return new BlobUriBuilder(blob.Uri)
            {
                Sas = sas.ToSasQueryParameters(key.Value, _service.AccountName),
            }.ToUri();
        }
        catch (RequestFailedException)
        {
            // Anonymous or SAS-authenticated clients cannot sign URLs.
            return null;
        }
    }

    /// <summary>
    /// Move a stored file to another access tier (Hot, Cool, Cold, Archive). Moving out of Archive starts
    /// a rehydration that can take hours; the file cannot be read until it completes.
    /// </summary>
    public async Task SetAccessTierAsync(IFileEntry entry, AccessTier tier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.FileLocation))
            throw new StorageValidationException("File entry has no FileLocation.");
        await _container.GetBlobClient(entry.FileLocation)
            .SetAccessTierAsync(tier, rehydratePriority: rehydratePriority, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerReady)
            return;

        await _containerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_containerReady)
            {
                await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);
                _containerReady = true;
            }
        }
        finally
        {
            _containerGate.Release();
        }
    }

    internal static BlobServiceClient CreateServiceClient(AzureBlobOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            return new BlobServiceClient(options.ConnectionString);
        if (options.ServiceUri is not null)
            return new BlobServiceClient(options.ServiceUri);
        throw new StorageConfigurationException(
            "Configure Storage:Azure:ConnectionString or Storage:Azure:ServiceUri, or register a BlobServiceClient in DI.");
    }

    private static AccessTier? ParseTier(string? tier)
        => string.IsNullOrWhiteSpace(tier) ? null : new AccessTier(char.ToUpperInvariant(tier.Trim()[0]) + tier.Trim()[1..].ToLowerInvariant());
}
