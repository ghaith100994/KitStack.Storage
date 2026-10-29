using Azure.Storage.Blobs;
using KitStack.Storage.Azure.Options;
using KitStack.Storage.Azure.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Azure.HealthChecks;

/// <summary>Health check that reads the properties of the configured container.</summary>
public sealed class AzureBlobHealthCheck : IHealthCheck
{
    private readonly BlobContainerClient _container;

    public AzureBlobHealthCheck(IOptions<AzureBlobOptions> options, BlobServiceClient? serviceClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _container = (serviceClient ?? AzureBlobFileStorageManager.CreateServiceClient(options.Value))
            .GetBlobContainerClient(options.Value.ContainerName);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var exists = await _container.ExistsAsync(cancellationToken).ConfigureAwait(false);
            return exists.Value
                ? HealthCheckResult.Healthy($"Container: {_container.Name}")
                : new HealthCheckResult(context.Registration.FailureStatus, $"Container '{_container.Name}' does not exist.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
