using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using KitStack.Storage.S3.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text;

namespace KitStack.Storage.S3.HealthChecks;

public sealed class S3HealthCheck : IHealthCheck, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly S3Options _options;
    private bool _disposed;

    public S3HealthCheck(S3Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(_options.MainTarget.ServiceUrl))
        {
            config.ServiceURL = _options.MainTarget.ServiceUrl;
            config.UseHttp = _options.MainTarget.ServiceUrl.StartsWith("http:", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(_options.MainTarget.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(_options.MainTarget.Region);
        }

        // Create a client using default credentials chain; provider apps can provide credentials via environment/SDK chain
        _client = new AmazonS3Client(config);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var testKey = (string.IsNullOrWhiteSpace(_options.MainTarget.Prefix) ? string.Empty : _options.MainTarget.Prefix.Trim('/') + "/") +
                          $"healthcheck-{DateTime.UtcNow:yyyy-MM-dd-HH-mm-ss}-{Guid.NewGuid():N}.tmp";

            using var transfer = new TransferUtility(_client);
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("ok"));

            var uploadRequest = new TransferUtilityUploadRequest
            {
                BucketName = _options.MainTarget.BucketName,
                Key = testKey,
                InputStream = ms
            };

            await transfer.UploadAsync(uploadRequest, cancellationToken).ConfigureAwait(false);

            // Attempt to delete the test object
            await _client.DeleteObjectAsync(_options.MainTarget.BucketName, testKey, cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy($"Bucket: {_options.MainTarget.BucketName}");
        }
        catch (OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, description: "S3 health check cancelled");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _client?.Dispose();
            }
            _disposed = true;
        }
    }
}
