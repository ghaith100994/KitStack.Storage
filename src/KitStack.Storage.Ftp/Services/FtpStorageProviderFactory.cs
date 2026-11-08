using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Ftp.HealthChecks;
using KitStack.Storage.Ftp.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KitStack.Storage.Ftp.Services;

/// <summary>Builds FTP/FTPS instances (<c>ProviderType = "Ftp"</c>, settings bind to <see cref="FtpOptions"/>).</summary>
public sealed class FtpStorageProviderFactory : StorageProviderFactory<FtpOptions>
{
    /// <summary>Provider type key.</summary>
    public const string Type = "Ftp";

    /// <inheritdoc/>
    public override string ProviderType => Type;

    /// <inheritdoc/>
    public override StorageCapabilities Capabilities => StorageCapabilities.Overwrite;

    /// <inheritdoc/>
    protected override IFileStorageManager Create(StorageProviderContext context, FtpOptions options)
        => new FtpFileStorageManager(Wrap(options), Wrap(context.StorageOptions));

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, FtpOptions options, CancellationToken cancellationToken)
        => RunHealthCheckAsync(new FtpHealthCheck(Wrap(options)), $"kitstack_storage_{context.Name}", cancellationToken);
}
