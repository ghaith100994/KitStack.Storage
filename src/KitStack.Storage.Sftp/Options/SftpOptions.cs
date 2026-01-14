using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;

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

// Image processing options used by the FTP manager when creating variants.
public class ImageProcessingOptions
{
    public bool CreateThumbnail { get; set; } = true;
    public int ThumbnailMaxWidth { get; set; } = 200;
    public int ThumbnailMaxHeight { get; set; } = 200;
    public bool CreateCompressed { get; set; } = true;
    public int CompressedMaxWidth { get; set; } = 1200;
    public int CompressedMaxHeight { get; set; } = 1200;
    public int JpegQuality { get; set; } = 85;

    public IList<ImageSizeOption> AdditionalSizes { get; set; } = [];
}

public class ImageSizeOption
{
    public string SizeName { get; set; } = string.Empty;
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
    public int JpegQuality { get; set; } = 80;
}
