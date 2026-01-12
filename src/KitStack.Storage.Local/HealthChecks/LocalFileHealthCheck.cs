using System.Text;
using KitStack.Storage.Local.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Local.HealthChecks;

/// <summary>
/// Health check that verifies the local storage path is writable.
/// </summary>
public class LocalFileHealthCheck : IHealthCheck
{
    private readonly LocalOptions _options;

    public LocalFileHealthCheck(IOptions<LocalOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var basePath = Path.IsPathRooted(_options.Path)
                ? _options.Path
                : Path.Combine(Directory.GetCurrentDirectory(), _options.Path);

            if (!Directory.Exists(basePath))
                Directory.CreateDirectory(basePath);

            var testFile = Path.Combine(basePath, $"healthcheck-{Guid.NewGuid():N}.tmp");
            File.WriteAllTextAsync(testFile, "ok", Encoding.UTF8, cancellationToken);
            File.Delete(testFile);

            return Task.FromResult(HealthCheckResult.Healthy($"Path: {basePath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, exception: ex));
        }
    }
}