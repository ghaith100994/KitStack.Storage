using System.Diagnostics;
using KitStack.Storage.Abstractions.Exceptions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KitStack.Storage.Abstractions.Instances;

/// <summary>Validates a definition and probes connectivity before it is saved ("test connection"). Nothing is cached.</summary>
public interface IStorageInstanceValidator
{
    /// <summary>Validate the provider type, secrets, provider options and storage options, then probe connectivity.</summary>
    Task<StorageInstanceTestResult> TestAsync(StorageInstanceDefinition definition, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IStorageInstanceValidator.TestAsync"/>. Messages never contain secret values.</summary>
public sealed class StorageInstanceTestResult
{
    /// <summary>True when the definition is valid and the probe reported <see cref="HealthStatus.Healthy"/> or <see cref="HealthStatus.Degraded"/>.</summary>
    public bool Succeeded => Errors.Count == 0 && Status is HealthStatus.Healthy or HealthStatus.Degraded;

    /// <summary>Validation errors (empty when the definition is valid).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Probe status, or <c>null</c> when validation failed and no probe ran.</summary>
    public HealthStatus? Status { get; init; }

    /// <summary>Probe description (redacted).</summary>
    public string? Description { get; init; }

    /// <summary>How long the test took.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>Checks the health of a storage instance by name with its provider's health-check logic.</summary>
public interface IStorageHealthProbe
{
    /// <summary>Probe the instance <paramref name="storageName"/>. Unknown names report <see cref="HealthStatus.Unhealthy"/>.</summary>
    Task<HealthCheckResult> CheckAsync(string storageName, CancellationToken cancellationToken = default);
}

internal sealed class StorageInstanceValidator(StorageInstanceResolver resolver) : IStorageInstanceValidator
{
    public async Task<StorageInstanceTestResult> TestAsync(StorageInstanceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var watch = Stopwatch.StartNew();
        ResolvedStorageInstance resolved;
        try
        {
            resolved = await resolver.ResolveAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageInstanceValidationException ex)
        {
            return new StorageInstanceTestResult { Errors = ex.Errors, Duration = watch.Elapsed };
        }

        var errors = StorageInstanceResolver.Validate(resolved);
        if (errors.Count > 0)
            return new StorageInstanceTestResult { Errors = errors, Duration = watch.Elapsed };

        var probe = await StorageHealthProbe.ProbeAsync(resolved, cancellationToken).ConfigureAwait(false);
        return new StorageInstanceTestResult { Status = probe.Status, Description = probe.Description, Duration = watch.Elapsed };
    }
}

internal sealed class StorageHealthProbe(FileStorageManagerProvider provider) : IStorageHealthProbe
{
    public async Task<HealthCheckResult> CheckAsync(string storageName, CancellationToken cancellationToken = default)
    {
        ResolvedStorageInstance? resolved;
        try
        {
            resolved = await provider.ResolveForProbeAsync(storageName, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageConfigurationException ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }

        return resolved is null
            ? HealthCheckResult.Unhealthy($"Storage instance '{StorageInstance.Normalize(storageName)}' has no provider factory or definition to probe.")
            : await ProbeAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run the factory probe; exceptions are replaced by redacted descriptions.</summary>
    internal static async Task<HealthCheckResult> ProbeAsync(ResolvedStorageInstance resolved, CancellationToken cancellationToken)
    {
        HealthCheckResult result;
        try
        {
            result = await resolved.Factory.ProbeAsync(resolved.Context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(resolved.Redactor.Redact($"{ex.GetType().Name}: {ex.Message}"));
        }

        var description = result.Description;
        if (result.Exception is { } exception)
            description = string.IsNullOrEmpty(description) ? $"{exception.GetType().Name}: {exception.Message}" : $"{description} ({exception.GetType().Name}: {exception.Message})";
        return new HealthCheckResult(result.Status, resolved.Redactor.Redact(description), exception: null, result.Data);
    }
}

/// <summary>Health check of one storage instance, registered by <c>AddKitStackStorageHealthChecks()</c>.</summary>
public sealed class StorageInstanceHealthCheck(IStorageHealthProbe probe, string storageName) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await probe.CheckAsync(storageName, cancellationToken).ConfigureAwait(false);
        return result.Status == HealthStatus.Unhealthy
            ? new HealthCheckResult(context.Registration.FailureStatus, result.Description, result.Exception, result.Data)
            : result;
    }
}
