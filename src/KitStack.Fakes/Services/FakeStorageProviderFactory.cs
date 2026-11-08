using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Fakes.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KitStack.Fakes.Services;

/// <summary>Builds isolated in-memory instances (<c>ProviderType = "Fake"</c>, settings bind to <see cref="FakeOptions"/>).</summary>
public sealed class FakeStorageProviderFactory : StorageProviderFactory<FakeOptions>
{
    /// <summary>Provider type key.</summary>
    public const string Type = "Fake";

    /// <inheritdoc/>
    public override string ProviderType => Type;

    /// <inheritdoc/>
    public override StorageCapabilities Capabilities => StorageCapabilities.PublicUrls | StorageCapabilities.Overwrite;

    /// <inheritdoc/>
    protected override IFileStorageManager Create(StorageProviderContext context, FakeOptions options)
        => new InMemoryFileStorageManager(Wrap(options), Wrap(context.StorageOptions));

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, FakeOptions options, CancellationToken cancellationToken)
        => Task.FromResult(HealthCheckResult.Healthy("In-memory storage"));
}
