using KitStack.Storage.Ftp.Options;
using KitStack.Storage.Ftp.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Ftp.HealthChecks;

/// <summary>Health check that connects to the FTP server and lists the remote base path.</summary>
public sealed class FtpHealthCheck : IHealthCheck
{
    private readonly FtpOptions _options;

    public FtpHealthCheck(IOptions<FtpOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var client = FtpClientFactory.Create(_options);
            await using (client.ConfigureAwait(false))
            {
                await client.Connect(cancellationToken).ConfigureAwait(false);
                var path = string.IsNullOrWhiteSpace(_options.RemotePath) ? "Files" : _options.RemotePath.Trim('/');
                if (await client.DirectoryExists(path, cancellationToken).ConfigureAwait(false))
                    await client.GetListing(path, cancellationToken).ConfigureAwait(false);
                await client.Disconnect(cancellationToken).ConfigureAwait(false);
            }

            return HealthCheckResult.Healthy("FTP reachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
