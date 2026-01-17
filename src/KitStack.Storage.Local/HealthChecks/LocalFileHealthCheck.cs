using System.Text;
using KitStack.Storage.Local.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Local.HealthChecks;

/// <summary>
/// Health check that verifies the local storage path is writable.
/// </summary>
public sealed class LocalFileHealthCheck : IHealthCheck
{
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<LocalOptions> _optionsMonitor;

    public LocalFileHealthCheck(Microsoft.Extensions.Options.IOptionsMonitor<LocalOptions> options)
    {
        _optionsMonitor = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var opts = _optionsMonitor.CurrentValue ?? new LocalOptions();
            var basePath = Path.IsPathRooted(opts.Path)
                ? opts.Path
                : Path.Combine(Directory.GetCurrentDirectory(), opts.Path);

            // Ensure directory exists
            if (!Directory.Exists(basePath))
                Directory.CreateDirectory(basePath);

            var testFile = Path.Combine(basePath, $"healthcheck-{Guid.NewGuid():N}.tmp");

            try
            {
                // Write and await to ensure the file is actually created and flushed
                await File.WriteAllTextAsync(testFile, "ok", Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Attempt to clean up the test file; ignore cleanup errors
                try
                {
                    if (File.Exists(testFile))
                        File.Delete(testFile);
                }
                catch
                {
                    // Swallow cleanup exceptions - they don't change the health outcome
                }
            }

            // Healthy result with base path in description
            return HealthCheckResult.Healthy($"Path: {basePath}");
        }
        catch (OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, description: "Health check cancelled");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
