using KitStack.Storage.Sftp.Options;
using KitStack.Storage.Sftp.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Sftp.HealthChecks;

/// <summary>Health check that opens an SFTP session and checks the remote base path.</summary>
public sealed class SftpHealthCheck : IHealthCheck
{
    private readonly SftpOptions _options;

    public SftpHealthCheck(IOptions<SftpOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            using var client = SftpClientFactory.Create(_options);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var path = string.IsNullOrWhiteSpace(_options.RemotePath) ? "Files" : _options.RemotePath;
            var exists = await client.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
            client.Disconnect();
            return exists || _options.EnsureRemotePathExists
                ? HealthCheckResult.Healthy("SFTP reachable")
                : new HealthCheckResult(context.Registration.FailureStatus, $"Remote path '{path}' does not exist.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
