using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Abstractions.Instances;

/// <summary>A statically registered instance (<c>AddS3("name")</c>, or the default one added by <c>AddS3()</c>).</summary>
internal sealed record StaticStorageInstance(string Name, string ProviderType, string OptionsName)
{
    public bool IsDefault => StorageInstance.IsDefault(Name);
}

/// <summary>A definition ready for its factory.</summary>
internal sealed record ResolvedStorageInstance(IStorageProviderFactory Factory, StorageProviderContext Context, SecretRedactor Redactor, string? Version);

/// <summary>Turns definitions and static registrations into factory contexts: resolves secrets, merges storage options.</summary>
internal sealed class StorageInstanceResolver(
    IServiceProvider services,
    IEnumerable<IStorageProviderFactory> factories,
    IStorageSecretResolver secrets,
    IOptions<StorageOptions> globalOptions)
{
    private readonly IReadOnlyList<IStorageProviderFactory> _factories = factories.ToList();

    public IStorageProviderFactory? FindFactory(string providerType)
        => _factories.LastOrDefault(f => string.Equals(f.ProviderType, providerType?.Trim(), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyCollection<string> ProviderTypes => _factories.Select(f => f.ProviderType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<ResolvedStorageInstance> ResolveAsync(StorageInstanceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var factory = FindFactory(definition.ProviderType)
            ?? throw new StorageInstanceValidationException(definition.Name,
                [$"Unknown provider type '{definition.ProviderType}'. Registered types: {string.Join(", ", ProviderTypes)}."]);

        var redactor = new SecretRedactor();
        redactor.AddSecretLikeValues(definition.Settings);

        var overrides = Copy(definition.Storage);
        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var (field, reference) in definition.SecretReferences)
        {
            var value = string.IsNullOrWhiteSpace(reference) ? null : await secrets.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                errors.Add($"Secret reference '{reference}' for '{field}' could not be resolved.");
                continue;
            }

            redactor.Add(value);
            if (field.StartsWith(StorageSecretFields.StoragePrefix, StringComparison.OrdinalIgnoreCase))
                ApplyStorageSecret(overrides, field[StorageSecretFields.StoragePrefix.Length..], value, errors, reference);
            else
                overlay[field] = value;
        }

        if (errors.Count > 0)
            throw new StorageInstanceValidationException(definition.Name, errors);

        var settings = new ConfigurationBuilder()
            .AddConfiguration(definition.Settings)
            .AddInMemoryCollection(overlay)
            .Build();

        var storage = StorageOptionsOverrides.Merge(globalOptions.Value, overrides, definition.Name);
        redactor.Add(storage.MasterEncryptionKey);
        foreach (var key in storage.PreviousEncryptionKeys)
            redactor.Add(key);

        return new ResolvedStorageInstance(
            factory,
            new StorageProviderContext(definition.Name, factory.ProviderType, settings, optionsName: null, storage, services),
            redactor,
            definition.Version);
    }

    public ResolvedStorageInstance ResolveStatic(StaticStorageInstance instance)
    {
        var factory = FindFactory(instance.ProviderType)
            ?? throw new StorageConfigurationException($"No provider factory is registered for '{instance.ProviderType}'.");

        StorageOptions storage;
        if (instance.IsDefault)
        {
            storage = globalOptions.Value;
        }
        else
        {
            var overrides = services.GetService<IOptionsMonitor<StorageOptionsOverrides>>()?.Get(instance.Name);
            storage = StorageOptionsOverrides.Merge(globalOptions.Value, overrides, instance.Name);
        }

        var redactor = new SecretRedactor();
        redactor.Add(storage.MasterEncryptionKey);
        return new ResolvedStorageInstance(
            factory,
            new StorageProviderContext(instance.Name, factory.ProviderType, settings: null, instance.OptionsName, storage, services),
            redactor,
            Version: null);
    }

    /// <summary>Provider option errors plus storage option errors, redacted.</summary>
    public static IReadOnlyList<string> Validate(ResolvedStorageInstance resolved)
    {
        var errors = new List<string>(resolved.Factory.Validate(resolved.Context));
        try
        {
            _ = AesGcmFileContentEncryptor.FromOptions(resolved.Context.StorageOptions);
        }
        catch (StorageConfigurationException ex)
        {
            errors.Add(ex.Message);
        }

        if (resolved.Context.StorageOptions.Upload is { MaxBytes: < 0 })
            errors.Add("Upload:MaxBytes must be 0 (unlimited) or positive.");
        return errors.Select(resolved.Redactor.Redact).ToList();
    }

    private static void ApplyStorageSecret(StorageOptionsOverrides overrides, string path, string value, List<string> errors, string reference)
    {
        const string previousPrefix = "PreviousEncryptionKeys";
        if (string.Equals(path, nameof(StorageOptionsOverrides.MasterEncryptionKey), StringComparison.OrdinalIgnoreCase))
            overrides.MasterEncryptionKey = value;
        else if (path.StartsWith(previousPrefix, StringComparison.OrdinalIgnoreCase))
            overrides.PreviousEncryptionKeys.Add(value);
        else
            errors.Add($"Secret reference '{reference}' targets unsupported storage field '{path}'.");
    }

    private static StorageOptionsOverrides Copy(StorageOptionsOverrides? source)
    {
        var copy = new StorageOptionsOverrides
        {
            Upload = source?.Upload is null ? null : StorageOptionsOverrides.Clone(source.Upload),
            MasterEncryptionKey = source?.MasterEncryptionKey,
            DisableEncryption = source?.DisableEncryption ?? false,
        };
        foreach (var key in source?.PreviousEncryptionKeys ?? [])
            copy.PreviousEncryptionKeys.Add(key);
        return copy;
    }
}
