using FluentFTP;
using KitStack.Storage.Ftp.Options;

namespace KitStack.Storage.Ftp.Services;

internal static class FtpClientFactory
{
    public static AsyncFtpClient Create(FtpOptions options)
    {
        // FluentFTP rejects an empty user name; anonymous FTP logs in as "anonymous".
        var anonymous = string.IsNullOrWhiteSpace(options.Username);
        var client = new AsyncFtpClient(
            options.Host,
            anonymous ? "anonymous" : options.Username!,
            anonymous ? "anonymous" : options.Password ?? string.Empty,
            options.Port);
        if (options.UseSsl)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.ValidateAnyCertificate = options.ValidateAnyCertificate;
        }

        return client;
    }
}
