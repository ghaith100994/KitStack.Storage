using Azure.Storage.Blobs;
using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Azure.HealthChecks;
using KitStack.Storage.Azure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KitStack.Storage.Azure.Services;

/// <summary>
/// Builds Azure Blob instances (<c>ProviderType = "Azure"</c>, settings bind to <see cref="AzureBlobOptions"/>).
/// An instance without <c>ConnectionString</c> or <c>ServiceUri</c> uses the <see cref="BlobServiceClient"/> registered in DI.
/// </summary>
public sealed class AzureBlobStorageProviderFactory : StorageProviderFactory<AzureBlobOptions>
{
    /// <summary>Provider type key.</summary>
    public const string Type = "Azure";

    /// <inheritdoc/>
    public override string ProviderType => Type;

    /// <inheritdoc/>
    public override StorageCapabilities Capabilities => StorageCapabilities.SignedUrls | StorageCapabilities.Overwrite | StorageCapabilities.AccessTiers;

    /// <inheritdoc/>
    protected override IFileStorageManager Create(StorageProviderContext context, AzureBlobOptions options)
        => new AzureBlobFileStorageManager(Wrap(options), Wrap(context.StorageOptions), encryptor: null, ServiceClient(context, options));

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, AzureBlobOptions options, CancellationToken cancellationToken)
        => RunHealthCheckAsync(new AzureBlobHealthCheck(Wrap(options), ServiceClient(context, options)), $"kitstack_storage_{context.Name}", cancellationToken);

    private static BlobServiceClient? ServiceClient(StorageProviderContext context, AzureBlobOptions options)
        => string.IsNullOrWhiteSpace(options.ConnectionString) && options.ServiceUri is null
            ? context.Services.GetService<BlobServiceClient>()
            : null;
}
