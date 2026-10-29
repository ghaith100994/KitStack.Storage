using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
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
        builder.Services.AddAzureBlobStorageManager(builder.Configuration);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHealthChecks().AddCheck<AzureBlobHealthCheck>("kitstack_azure_blob");
        return builder;
    }

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
