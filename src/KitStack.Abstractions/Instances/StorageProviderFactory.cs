using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KitStack.Abstractions.Instances;

/// <summary>
/// Builds <see cref="IFileStorageManager"/> instances of one provider type. Each provider package registers its own
/// (<c>AddLocalProvider()</c>, <c>AddS3Provider()</c>, ..., also done by <c>AddLocal()</c>, <c>AddS3()</c>, ...), so
/// provider-specific construction stays in that package.
/// </summary>
public interface IStorageProviderFactory
{
    /// <summary>Provider type key (<c>Local</c>, <c>S3</c>, <c>Azure</c>, <c>Sftp</c>, <c>Ftp</c>, <c>Fake</c>), case-insensitive.</summary>
    string ProviderType { get; }

    /// <summary>What the provider type supports.</summary>
    StorageCapabilities Capabilities { get; }

    /// <summary>What an instance with these settings supports (a subset of <see cref="Capabilities"/>).</summary>
    StorageCapabilities GetCapabilities(StorageProviderContext context);

    /// <summary>Bind and validate the provider options (DataAnnotations / <see cref="IValidatableObject"/>). Empty when valid.</summary>
    IReadOnlyList<string> Validate(StorageProviderContext context);

    /// <summary>Build a manager. The caller owns it (and disposes it when it is <see cref="IDisposable"/> / <see cref="IAsyncDisposable"/>).</summary>
    IFileStorageManager Create(StorageProviderContext context);

    /// <summary>Connectivity probe using the provider's health check logic, without building a cached instance.</summary>
    Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, CancellationToken cancellationToken = default);
}

/// <summary>Everything a factory needs to build or probe one instance.</summary>
public sealed class StorageProviderContext
{
    /// <summary>Create a context.</summary>
    /// <param name="name">Instance name.</param>
    /// <param name="providerType">Provider type.</param>
    /// <param name="settings">Provider settings with secrets applied; <c>null</c> for statically registered instances, which use named options.</param>
    /// <param name="optionsName">Named-options name used when <paramref name="settings"/> is <c>null</c> (empty for the default instance).</param>
    /// <param name="storageOptions">Resolved storage options of the instance (upload policy, keys, <see cref="StorageOptions.InstanceName"/>).</param>
    /// <param name="services">Application services.</param>
    public StorageProviderContext(string name, string providerType, IConfiguration? settings, string? optionsName, StorageOptions storageOptions, IServiceProvider services)
    {
        Name = name;
        ProviderType = providerType;
        Settings = settings;
        OptionsName = optionsName;
        StorageOptions = storageOptions;
        Services = services;
    }

    /// <summary>Instance name.</summary>
    public string Name { get; }

    /// <summary>Provider type.</summary>
    public string ProviderType { get; }

    /// <summary>Provider settings (secrets applied), or <c>null</c> for statically registered instances.</summary>
    public IConfiguration? Settings { get; }

    /// <summary>Named-options name for statically registered instances (<c>Options.DefaultName</c> for <c>default</c>).</summary>
    public string? OptionsName { get; }

    /// <summary>Resolved storage options of the instance.</summary>
    public StorageOptions StorageOptions { get; }

    /// <summary>Application services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>True when content written by the instance is encrypted by the library.</summary>
    public bool Encrypts => !string.IsNullOrWhiteSpace(StorageOptions.MasterEncryptionKey);

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({ProviderType})";
}

/// <summary>
/// Base class for provider factories over an options type: binds <typeparamref name="TOptions"/> from the settings
/// (or named options for static instances), validates it, and hands it to <see cref="Create(StorageProviderContext, TOptions)"/>.
/// </summary>
public abstract class StorageProviderFactory<TOptions> : IStorageProviderFactory
    where TOptions : class, new()
{
    /// <inheritdoc/>
    public abstract string ProviderType { get; }

    /// <inheritdoc/>
    public abstract StorageCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public StorageCapabilities GetCapabilities(StorageProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var capabilities = GetCapabilities(context, BindOptions(context));
        // Encrypted content is never handed out through direct URLs.
        if (context.Encrypts)
            capabilities &= ~(StorageCapabilities.SignedUrls | StorageCapabilities.PublicUrls);
        return capabilities;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Validate(StorageProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TOptions options;
        try
        {
            options = BindOptions(context);
        }
        catch (OptionsValidationException ex)
        {
            return ex.Failures.ToList();
        }
        catch (InvalidOperationException ex)
        {
            // Binder conversion errors ("Failed to convert configuration value ... to type ...").
            return [ex.Message];
        }

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid options.").ToList();
    }

    /// <inheritdoc/>
    public IFileStorageManager Create(StorageProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(context, BindOptions(context));
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProbeAsync(context, BindOptions(context), cancellationToken);
    }

    /// <summary>Build the manager from bound options.</summary>
    protected abstract IFileStorageManager Create(StorageProviderContext context, TOptions options);

    /// <summary>Probe connectivity with bound options.</summary>
    protected abstract Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, TOptions options, CancellationToken cancellationToken);

    /// <summary>Capabilities of an instance with these options. Default: <see cref="Capabilities"/>.</summary>
    protected virtual StorageCapabilities GetCapabilities(StorageProviderContext context, TOptions options) => Capabilities;

    /// <summary>
    /// Bind the options: from <see cref="StorageProviderContext.Settings"/> for dynamic instances, otherwise from the
    /// named options registered for the static instance (<see cref="IOptions{TOptions}"/> for <c>default</c>).
    /// </summary>
    public TOptions BindOptions(StorageProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Settings is not null)
        {
            var options = new TOptions();
            context.Settings.Bind(options);
            return options;
        }

        if (string.IsNullOrEmpty(context.OptionsName))
            return context.Services.GetService<IOptions<TOptions>>()?.Value ?? new TOptions();
        return context.Services.GetRequiredService<IOptionsMonitor<TOptions>>().Get(context.OptionsName);
    }

    /// <summary>Wrap <paramref name="options"/> for provider constructors.</summary>
    protected static IOptions<T> Wrap<T>(T options) where T : class => Microsoft.Extensions.Options.Options.Create(options);

    /// <summary>Run an existing <see cref="IHealthCheck"/> outside the health-check service.</summary>
    protected static async Task<HealthCheckResult> RunHealthCheckAsync(IHealthCheck check, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        try
        {
            var context = new HealthCheckContext { Registration = new HealthCheckRegistration(name, check, HealthStatus.Unhealthy, null) };
            return await check.CheckHealthAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            switch (check)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}
