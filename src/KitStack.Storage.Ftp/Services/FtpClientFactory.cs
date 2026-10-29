using FluentFTP;
using KitStack.Storage.Ftp.Options;

namespace KitStack.Storage.Ftp.Services;

internal static class FtpClientFactory
{
    public static AsyncFtpClient Create(FtpOptions options)
    {
        var client = new AsyncFtpClient(options.Host, options.Username ?? string.Empty, options.Password ?? string.Empty, options.Port);
        if (options.UseSsl)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.ValidateAnyCertificate = options.ValidateAnyCertificate;
        }

        return client;
    }
}
