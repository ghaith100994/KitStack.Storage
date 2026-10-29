using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Ftp.HealthChecks;
using KitStack.Storage.Ftp.Options;
using KitStack.Storage.Ftp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Ftp.Extensions;

public static class FtpServiceCollectionExtensions
{
    /// <summary>
    /// Register the FTP provider from the <c>Ftp</c> sub-section of the storage configuration
    /// (<c>Storage:Ftp</c>), plus a <c>kitstack_ftp</c> health check.
    /// </summary>
    public static KitStackStorageBuilder AddFtp(this KitStackStorageBuilder builder, Action<FtpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddFtpStorageManager(builder.Configuration);
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHealthChecks().AddCheck<FtpHealthCheck>("kitstack_ftp");
        return builder;
    }

    public static IServiceCollection AddFtpStorageManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Ftp");
        services.AddOptions<FtpOptions>().Configure(section.Bind).Validate(Validate, "Invalid Storage:Ftp options.").ValidateOnStart();

        services.AddSingleton<FtpHealthCheck>();
        services.AddSingleton<IFileStorageManager, FtpFileStorageManager>();
        return services;
    }

    public static IServiceCollection AddFtpStorageManager(this IServiceCollection services, FtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
            throw new OptionsValidationException(nameof(FtpOptions), typeof(FtpOptions), results.Select(r => r.ErrorMessage ?? string.Empty));

        services.AddSingleton(_ => Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<FtpHealthCheck>();
        services.AddSingleton<IFileStorageManager, FtpFileStorageManager>();
        return services;
    }

    private static bool Validate(FtpOptions options)
        => Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
