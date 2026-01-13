using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.S3.HealthChecks;
using KitStack.Storage.S3.Options;
using KitStack.Storage.S3.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.S3.Extensions;

public static class S3ServiceCollectionExtensions
{
    public static IServiceCollection AddS3StorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("S3");

        services.AddOptions<S3Options>()
                .Configure(section.Bind)
                .ValidateDataAnnotations()
                .ValidateOnStart();


        services.AddSingleton<S3HealthCheck>();
        services.AddSingleton<IFileStorageManager, S3FileStorageManager>();
        services.AddSingleton<IS3PresignedUrlGenerator, S3PresignedUrlGenerator>();
        return services;
    }

    public static IServiceCollection AddS3StorageManager(this IServiceCollection services, S3Options options)
    {
        // Validate options instance immediately so errors surface during startup registration
        var results = new List<ValidationResult>();
        var ctx = new ValidationContext(options);
        if (!Validator.TryValidateObject(options, ctx, results, validateAllProperties: true))
        {
            throw new OptionsValidationException(nameof(S3Options), typeof(S3Options), results.Select(r => r.ErrorMessage));
        }

        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));

        services.AddSingleton<S3HealthCheck>();
        services.AddSingleton<IFileStorageManager, S3FileStorageManager>();
        services.AddSingleton<IS3PresignedUrlGenerator, S3PresignedUrlGenerator>();
        return services;
    }
}
