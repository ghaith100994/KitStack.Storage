using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.DependencyInjection;
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
    /// <summary>
    /// Register the SFTP provider from the <c>Sftp</c> sub-section of the storage configuration
    /// (<c>Storage:Sftp</c>), plus a <c>kitstack_sftp</c> health check.
    /// </summary>
    public static KitStackStorageBuilder AddSftp(this KitStackStorageBuilder builder, Action<SftpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSftpStorageManager(builder.Configuration);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHealthChecks().AddCheck<SftpHealthCheck>("kitstack_sftp");
        return builder;
    }

    public static IServiceCollection AddSftpStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Sftp");
        services.AddOptions<SftpOptions>().Configure(section.Bind).Validate(Validate, "Invalid Storage:Sftp options.").ValidateOnStart();

        services.AddSingleton<SftpHealthCheck>();
        services.AddSingleton<IFileStorageManager, SftpFileStorageManager>();
        return services;
    }

    public static IServiceCollection AddSftpStorageManager(this IServiceCollection services, SftpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
            throw new OptionsValidationException(nameof(SftpOptions), typeof(SftpOptions), results.Select(r => r.ErrorMessage ?? string.Empty));

        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<SftpHealthCheck>();
        services.AddSingleton<IFileStorageManager, SftpFileStorageManager>();
        return services;
    }

    private static bool Validate(SftpOptions options)
        => Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
