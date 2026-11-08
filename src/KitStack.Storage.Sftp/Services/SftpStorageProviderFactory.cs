using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Sftp.HealthChecks;
using KitStack.Storage.Sftp.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KitStack.Storage.Sftp.Services;

/// <summary>Builds SFTP instances (<c>ProviderType = "Sftp"</c>, settings bind to <see cref="SftpOptions"/>).</summary>
public sealed class SftpStorageProviderFactory : StorageProviderFactory<SftpOptions>
{
    /// <summary>Provider type key.</summary>
    public const string Type = "Sftp";

    /// <inheritdoc/>
    public override string ProviderType => Type;

    /// <inheritdoc/>
    public override StorageCapabilities Capabilities => StorageCapabilities.Overwrite;

    /// <inheritdoc/>
    protected override IFileStorageManager Create(StorageProviderContext context, SftpOptions options)
        => new SftpFileStorageManager(Wrap(options), Wrap(context.StorageOptions));

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, SftpOptions options, CancellationToken cancellationToken)
        => RunHealthCheckAsync(new SftpHealthCheck(Wrap(options)), $"kitstack_storage_{context.Name}", cancellationToken);
}
