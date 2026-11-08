using Amazon.S3;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.S3.HealthChecks;
using KitStack.Storage.S3.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KitStack.Storage.S3.Services;

/// <summary>
/// Creates the <see cref="IAmazonS3"/> client for a target. Register one to supply clients yourself (custom credential
/// providers, test doubles); by default clients are built from the target's region, service URL and credentials.
/// Clients created through it are owned (and disposed) by the manager that requested them.
/// </summary>
public interface IS3ClientFactory
{
    /// <summary>Create a client for <paramref name="target"/> of an instance configured with <paramref name="options"/>.</summary>
    IAmazonS3 CreateClient(S3TargetOptions target, S3Options options);
}

/// <summary>Builds S3 instances (<c>ProviderType = "S3"</c>, settings bind to <see cref="S3Options"/>).</summary>
public sealed class S3StorageProviderFactory : StorageProviderFactory<S3Options>
{
    /// <summary>Provider type key.</summary>
    public const string Type = "S3";

    /// <inheritdoc/>
    public override string ProviderType => Type;

    /// <inheritdoc/>
    public override StorageCapabilities Capabilities
        => StorageCapabilities.SignedUrls | StorageCapabilities.Overwrite | StorageCapabilities.ServerSideEncryption | StorageCapabilities.ImageVariantRouting;

    /// <inheritdoc/>
    protected override StorageCapabilities GetCapabilities(StorageProviderContext context, S3Options options)
    {
        var capabilities = Capabilities;
        if (options.MainTarget?.UseServerSideEncryption != true)
            capabilities &= ~StorageCapabilities.ServerSideEncryption;
        return capabilities;
    }

    /// <inheritdoc/>
    protected override IFileStorageManager Create(StorageProviderContext context, S3Options options)
        => new S3FileStorageManager(Wrap(options), ClientFactory(context.Services, options), Wrap(context.StorageOptions));

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, S3Options options, CancellationToken cancellationToken)
    {
        var factory = context.Services.GetService<IS3ClientFactory>();
        var check = factory is null
            ? new S3HealthCheck(Wrap(options))
            : new S3HealthCheck(options, factory.CreateClient(options.MainTarget, options));
        return RunHealthCheckAsync(check, $"kitstack_storage_{context.Name}", cancellationToken);
    }

    internal static Func<S3TargetOptions, IAmazonS3>? ClientFactory(IServiceProvider services, S3Options options)
        => services.GetService<IS3ClientFactory>() is { } factory ? target => factory.CreateClient(target, options) : null;
}
