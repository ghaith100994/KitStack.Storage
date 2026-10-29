using KitStack.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.EntityFrameworkCore.Hosting;

/// <summary>Runs <see cref="FileOrphanSweeper"/> on a fixed interval.</summary>
public sealed partial class FileOrphanSweeperHostedService(
    IServiceScopeFactory scopes,
    IOptions<OrphanSweeperOptions> options,
    ILogger<FileOrphanSweeperHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.Interval);
        do
        {
            try
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var sweeper = scope.ServiceProvider.GetRequiredService<FileOrphanSweeper>();
                    await sweeper.SweepAsync(settings.MinAge, settings.BatchSize, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Orphaned file sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
