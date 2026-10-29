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
        var expected = NormalizeFingerprint(options.HostKeyFingerprint);
        client.HostKeyReceived += (_, e) =>
            e.CanTrust = options.AcceptAnyHostKey
                         || (expected.Length > 0 && string.Equals(expected, NormalizeFingerprint(e.FingerPrintSHA256), StringComparison.Ordinal));
        return client;
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
