using KitStack.Storage.Sftp.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using FluentFTP;

namespace KitStack.Storage.Sftp.HealthChecks;

public sealed class SftpHealthCheck : IHealthCheck, IDisposable
{
    private readonly SftpOptions _options;
    private bool _disposed;

    public SftpHealthCheck(IOptions<SftpOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new FtpClient(_options.Host, _options.Username ?? string.Empty, _options.Password ?? string.Empty);
            client.Port = _options.Port;
            client.Connect();

            if (!string.IsNullOrWhiteSpace(_options.RemotePath))
            {
                var path = _options.RemotePath.Trim('/');
                var list = client.GetListing(path);
            }

            client.Disconnect();
            return Task.FromResult(HealthCheckResult.Healthy("FTP reachable"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, exception: ex));
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
            _disposed = true;
        }
    }
}
