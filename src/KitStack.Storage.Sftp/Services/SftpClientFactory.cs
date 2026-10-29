using System.Text;
using KitStack.Storage.Sftp.Options;
using Renci.SshNet;

namespace KitStack.Storage.Sftp.Services;

internal static class SftpClientFactory
{
    public static SftpClient Create(SftpOptions options)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(options.PrivateKey) || !string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            var keyFile = !string.IsNullOrWhiteSpace(options.PrivateKey)
                ? new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(options.PrivateKey)), options.PrivateKeyPassphrase)
                : new PrivateKeyFile(options.PrivateKeyPath!, options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(options.Username, keyFile));
        }

        if (!string.IsNullOrWhiteSpace(options.Password))
            methods.Add(new PasswordAuthenticationMethod(options.Username, options.Password));

        var connection = new ConnectionInfo(options.Host, options.Port, options.Username, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds),
        };

        var client = new SftpClient(connection);
        client.HostKeyReceived += (_, e) => e.CanTrust = IsTrusted(options, e.FingerPrintSHA256);
        return client;
    }

    /// <summary>The host key is trusted when it matches the configured fingerprint, or verification is explicitly off.</summary>
    internal static bool IsTrusted(SftpOptions options, string? presentedSha256)
    {
        if (options.AcceptAnyHostKey)
            return true;
        var expected = NormalizeFingerprint(options.HostKeyFingerprint);
        return expected.Length > 0 && string.Equals(expected, NormalizeFingerprint(presentedSha256), StringComparison.Ordinal);
    }

    // Accept "SHA256:abc", "abc" and "abc=" for the same key.
    internal static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return string.Empty;
        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];
        return value.TrimEnd('=');
    }
}
