using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Abstractions.Instances;

/// <summary>Resolves secret references of <see cref="StorageInstanceDefinition.SecretReferences"/>.</summary>
public interface IStorageSecretResolver
{
    /// <summary>The secret value for <paramref name="reference"/>, or <c>null</c> when it is unknown.</summary>
    ValueTask<string?> ResolveAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IStorageSecretResolver"/>: reads <c>StorageSecrets:{reference}</c> from the application's
/// <see cref="IConfiguration"/>, so environment variables (<c>StorageSecrets__hrm-s3-secret</c>), user secrets or any
/// configuration-backed secret store (Key Vault, AWS Secrets Manager providers, ...) work unchanged.
/// </summary>
public sealed class ConfigurationStorageSecretResolver : IStorageSecretResolver
{
    /// <summary>Configuration section that holds secrets.</summary>
    public const string SectionName = "StorageSecrets";

    private readonly IServiceProvider _services;

    /// <summary>Create the resolver over the <see cref="IConfiguration"/> registered in <paramref name="services"/>.</summary>
    public ConfigurationStorageSecretResolver(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc/>
    public ValueTask<string?> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var configuration = _services.GetService<IConfiguration>();
        var value = configuration?[$"{SectionName}:{reference}"];
        return ValueTask.FromResult(string.IsNullOrEmpty(value) ? null : value);
    }
}

/// <summary>Removes known secret values from text (exception messages, health descriptions).</summary>
public sealed class SecretRedactor
{
    /// <summary>Replacement for secret values.</summary>
    public const string Mask = "***";

    private static readonly string[] SecretKeyHints = ["password", "secret", "passphrase", "connectionstring", "privatekey", "accesskey", "token", "encryptionkey", "sas"];
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    /// <summary>Register a secret value.</summary>
    public void Add(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _values.Add(value);
    }

    /// <summary>Register the values of settings whose key looks like a secret (password, secret, connection string, ...).</summary>
    public void AddSecretLikeValues(IConfiguration settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var (key, value) in settings.AsEnumerable())
        {
            var last = key[(key.LastIndexOf(':') + 1)..];
            if (value is not null && SecretKeyHints.Any(h => last.Contains(h, StringComparison.OrdinalIgnoreCase)))
                Add(value);
        }
    }

    /// <summary><paramref name="text"/> with every registered secret replaced by <see cref="Mask"/>.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || _values.Count == 0)
            return text ?? string.Empty;
        foreach (var value in _values.OrderByDescending(v => v.Length))
            text = text.Replace(value, Mask, StringComparison.Ordinal);
        return text;
    }
}
