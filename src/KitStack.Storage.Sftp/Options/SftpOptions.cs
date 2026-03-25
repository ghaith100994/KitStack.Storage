using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using KitStack.Abstractions.Options;

namespace KitStack.Storage.Sftp.Options;

public class SftpOptions : IValidatableObject
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;

    // Credentials (optional for anonymous FTP)
    public string? Username { get; set; }
    public string? Password { get; set; }

    // FluentFTP specific options
    /// <summary>
    /// Use SSL/TLS (FTPS) when connecting.
    /// </summary>
    public bool UseSsl { get; set; }

    /// <summary>
    /// If true, accept any server certificate. Use only for testing.
    /// </summary>
    public bool ValidateAnyCertificate { get; set; }

    /// <summary>
    /// Remote base path (prefix) where files will be uploaded. Should not start with '/'.
    /// </summary>
    public string? RemotePath { get; set; }

    /// <summary>
    /// If true, try to create remote directories when uploading.
    /// </summary>
    public bool EnsureRemotePathExists { get; set; } = true;

    public ImageProcessingOptions? ImageProcessing { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        if (string.IsNullOrWhiteSpace(Host))
            results.Add(new ValidationResult("Host is required.", [nameof(Host)]));

        if (Port <= 0 || Port > 65535)
            results.Add(new ValidationResult("Port must be a positive number between 1 and 65535.", [nameof(Port)]));

        // If one of Username/Password is provided, require both
        if (!string.IsNullOrWhiteSpace(Username) ^ !string.IsNullOrWhiteSpace(Password))
            results.Add(new ValidationResult("Both Username and Password must be provided together (or neither) for anonymous access.", [nameof(Username), nameof(Password)]));

        return results;
    }
}
