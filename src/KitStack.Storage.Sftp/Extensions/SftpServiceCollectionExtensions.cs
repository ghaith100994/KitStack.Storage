using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Sftp.HealthChecks;
using KitStack.Storage.Sftp.Options;
using KitStack.Storage.Sftp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Sftp.Extensions;

public static class SftpServiceCollectionExtensions
{
    public static IServiceCollection AddSftpStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Sftp");
        services.AddOptions<SftpOptions>().Configure(section.Bind).ValidateOnStart();

        services.AddSingleton<SftpHealthCheck>();
        services.AddSingleton<IFileStorageManager, SftpFileStorageManager>();
        return services;
    }

    public static IServiceCollection AddSftpStorageManager(this IServiceCollection services, SftpOptions options)
    {
        var results = new List<ValidationResult>();
        var ctx = new ValidationContext(options);
        if (!Validator.TryValidateObject(options, ctx, results, validateAllProperties: true))
        {
            throw new OptionsValidationException(nameof(SftpOptions), typeof(SftpOptions), results.Select(r => r.ErrorMessage ?? string.Empty));
        }

        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<SftpHealthCheck>();
        services.AddSingleton<IFileStorageManager, SftpFileStorageManager>();
        return services;
    }
}
