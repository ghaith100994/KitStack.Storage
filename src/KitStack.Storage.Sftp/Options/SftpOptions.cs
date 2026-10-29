using System.ComponentModel.DataAnnotations;
using KitStack.Abstractions.Options;

namespace KitStack.Storage.Sftp.Options;

/// <summary>Options for the SFTP provider (bound from Storage:Sftp).</summary>
public class SftpOptions : IValidatableObject
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    /// <summary>Password authentication. Optional when a private key is configured.</summary>
    public string? Password { get; set; }

    /// <summary>Private key content (OpenSSH or PEM). Takes precedence over <see cref="PrivateKeyPath"/>.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Path to a private key file.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Passphrase of the private key, if it is encrypted.</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>
    /// Expected SHA-256 fingerprint of the server host key, as printed by <c>ssh-keygen -lf</c>
    /// (for example <c>SHA256:2l1d...</c>). Connections to a server presenting another key are refused.
    /// </summary>
    public string? HostKeyFingerprint { get; set; }

    /// <summary>
    /// Accept any host key. Only for testing: it allows a man-in-the-middle to read every file and the
    /// credentials. Either this or <see cref="HostKeyFingerprint"/> must be set.
    /// </summary>
    public bool AcceptAnyHostKey { get; set; }

    /// <summary>
    /// Remote base path. Relative paths are relative to the login directory; a leading '/' makes it absolute.
    /// Default "Files".
    /// </summary>
    public string? RemotePath { get; set; }

    /// <summary>Create missing remote directories when uploading.</summary>
    public bool EnsureRemotePathExists { get; set; } = true;

    /// <summary>Connection timeout in seconds.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    public ImageProcessingOptions? ImageProcessing { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        if (string.IsNullOrWhiteSpace(Host))
            results.Add(new ValidationResult("Host is required.", [nameof(Host)]));
        if (Port is <= 0 or > 65535)
            results.Add(new ValidationResult("Port must be between 1 and 65535.", [nameof(Port)]));
        if (string.IsNullOrWhiteSpace(Username))
            results.Add(new ValidationResult("Username is required.", [nameof(Username)]));
        if (string.IsNullOrWhiteSpace(Password) && string.IsNullOrWhiteSpace(PrivateKey) && string.IsNullOrWhiteSpace(PrivateKeyPath))
            results.Add(new ValidationResult("Configure Password, PrivateKey or PrivateKeyPath.", [nameof(Password), nameof(PrivateKey), nameof(PrivateKeyPath)]));
        if (string.IsNullOrWhiteSpace(HostKeyFingerprint) && !AcceptAnyHostKey)
            results.Add(new ValidationResult("Configure HostKeyFingerprint (or set AcceptAnyHostKey for testing only).", [nameof(HostKeyFingerprint), nameof(AcceptAnyHostKey)]));
        if (ConnectionTimeoutSeconds <= 0)
            results.Add(new ValidationResult("ConnectionTimeoutSeconds must be positive.", [nameof(ConnectionTimeoutSeconds)]));

        return results;
    }
}
