using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Storage.Local.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KitStack.Storage.Local.Services;

/// <summary>Builds Local instances (<c>ProviderType = "Local"</c>, settings bind to <see cref="LocalOptions"/>).</summary>
public sealed class LocalStorageProviderFactory : StorageProviderFactory<LocalOptions>
{
    /// <summary>Provider type key.</summary>
    public const string Type = "Local";

    /// <inheritdoc/>
    public override string ProviderType => Type;

    /// <inheritdoc/>
    public override StorageCapabilities Capabilities => StorageCapabilities.PublicUrls | StorageCapabilities.Overwrite;

    /// <inheritdoc/>
    protected override StorageCapabilities GetCapabilities(StorageProviderContext context, LocalOptions options)
        => string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? StorageCapabilities.Overwrite : Capabilities;

    /// <inheritdoc/>
    protected override IFileStorageManager Create(StorageProviderContext context, LocalOptions options)
        => new LocalFileStorageManager(Wrap(options), Wrap(context.StorageOptions));

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, LocalOptions options, CancellationToken cancellationToken)
        => RunHealthCheckAsync(new LocalFileHealthCheck(Wrap(options)), $"kitstack_storage_{context.Name}", cancellationToken);
}
