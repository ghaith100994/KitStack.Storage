using System.ComponentModel.DataAnnotations;
using KitStack.Storage.Abstractions.DependencyInjection;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Azure.HealthChecks;
using KitStack.Storage.Azure.Options;
using KitStack.Storage.Azure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Azure.Extensions;

public static class AzureBlobServiceCollectionExtensions
{
    /// <summary>
    /// Register the Azure Blob provider from the <c>Azure</c> sub-section of the storage configuration
    /// (<c>Storage:Azure</c>), plus a <c>kitstack_azure_blob</c> health check.
    /// </summary>
    public static KitStackStorageBuilder AddAzureBlob(this KitStackStorageBuilder builder, Action<AzureBlobOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddAzureBlobProvider().AddDefaultStorageInstance(AzureBlobStorageProviderFactory.Type);
        builder.Services.AddAzureBlobStorageManager(builder.Configuration);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHealthChecks().AddCheck<AzureBlobHealthCheck>("kitstack_azure_blob");
        return builder;
    }

    /// <summary>
    /// Register a named instance (an Azure Blob container), bound from <c>Storage:Instances:{name}:Azure</c> and <paramref name="configure"/>,
    /// with storage overrides from <c>Storage:Instances:{name}</c>. Resolve it with <c>IFileStorageManagerProvider.Get(name)</c>
    /// or <c>[FromKeyedServices(name)] IFileStorageManager</c>.
    /// </summary>
    public static KitStackStorageBuilder AddAzureBlob(this KitStackStorageBuilder builder, string name, Action<AzureBlobOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (StorageInstance.IsDefault(name))
            return builder.AddAzureBlob(configure);
        return builder.AddAzureBlobProvider().AddNamedStorageInstance(name, AzureBlobStorageProviderFactory.Type, "Azure", configure);
    }

    /// <summary>Register the provider factory so runtime instances with <c>ProviderType = "Azure"</c> can be built.</summary>
    public static KitStackStorageBuilder AddAzureBlobProvider(this KitStackStorageBuilder builder)
        => builder.AddStorageProviderFactory<AzureBlobStorageProviderFactory>();

    public static IServiceCollection AddAzureBlobStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Azure");
        services.AddOptions<AzureBlobOptions>().Configure(section.Bind).Validate(Validate, "Invalid Storage:Azure options.").ValidateOnStart();

        services.AddSingleton<AzureBlobHealthCheck>();
        services.AddSingleton<AzureBlobFileStorageManager>();
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<AzureBlobFileStorageManager>());
        return services;
    }

    public static IServiceCollection AddAzureBlobStorageManager(this IServiceCollection services, AzureBlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
            throw new OptionsValidationException(nameof(AzureBlobOptions), typeof(AzureBlobOptions), results.Select(r => r.ErrorMessage ?? string.Empty));

        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<AzureBlobHealthCheck>();
        services.AddSingleton<AzureBlobFileStorageManager>();
        services.AddSingleton<IFileStorageManager>(sp => sp.GetRequiredService<AzureBlobFileStorageManager>());
        return services;
    }

    private static bool Validate(AzureBlobOptions options)
        => Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
