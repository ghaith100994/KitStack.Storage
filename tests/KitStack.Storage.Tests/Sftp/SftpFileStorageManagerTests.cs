using System.Security.Cryptography;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Sftp.Extensions;
using KitStack.Storage.Sftp.HealthChecks;
using KitStack.Storage.Sftp.Options;
using KitStack.Storage.Sftp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests.Sftp;

public class SftpFileStorageManagerTests
{
    private readonly ISftpClient _client = Substitute.For<ISftpClient>();
    private readonly Dictionary<string, byte[]> _files = [];
    private readonly HashSet<string> _directories = ["Files"];
    private readonly List<string> _created = [];

    public SftpFileStorageManagerTests()
    {
        _client.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            _directories.Contains(ci.ArgAt<string>(0)) || _files.ContainsKey(ci.ArgAt<string>(0)));
        _client.CreateDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _directories.Add(ci.ArgAt<string>(0));
            _created.Add(ci.ArgAt<string>(0));
            return Task.CompletedTask;
        });
        _client.UploadFileAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<IProgress<UploadFileProgressReport>>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            Assert.True(ci.ArgAt<bool>(2)); // canOverride
            using var ms = new MemoryStream();
            ci.ArgAt<Stream>(0).CopyTo(ms);
            _files[ci.ArgAt<string>(1)] = ms.ToArray();
            return Task.CompletedTask;
        });
        _client.DownloadFileAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            if (!_files.TryGetValue(ci.ArgAt<string>(0), out var body))
                throw new SftpPathNotFoundException("missing");
            ci.ArgAt<Stream>(1).Write(body);
            return Task.CompletedTask;
        });
        _client.DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            _files.Remove(ci.ArgAt<string>(0)) ? Task.CompletedTask : throw new SftpPathNotFoundException("missing"));
    }

    private SftpFileStorageManager Create(Action<SftpOptions>? configure = null)
    {
        var options = new SftpOptions { Host = "h", Username = "u", Password = "p", AcceptAnyHostKey = true };
        configure?.Invoke(options);
        return new SftpFileStorageManager(MsOptions.Create(options), _client);
    }

    [Fact]
    public async Task Creates_missing_directories_level_by_level_once_and_reads_back()
    {
        using var storage = Create();
        var pdf = TestData.Pdf();

        var first = await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "a.pdf", null, "Legal/Contracts");
        await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "b.pdf", null, "Legal/Contracts");

        Assert.Equal(["Files/Legal", "Files/Legal/Contracts", "Files/Legal/Contracts/Invoice", "Files/Legal/Contracts/Invoice/Others"], _created);
        Assert.StartsWith("Files/Legal/Contracts/Invoice/Others/", first.FileLocation, StringComparison.Ordinal);
        Assert.Equal("Sftp", first.StorageProvider);
        Assert.True(await storage.ExistsAsync(first));

        await using var stream = await storage.OpenReadAsync(first);
        Assert.Equal(pdf, await TestData.ReadAllAsync(stream));
    }

    [Fact]
    public async Task Absolute_root_keeps_its_leading_slash()
    {
        using var storage = Create(o => { o.RemotePath = "/data/"; o.EnsureRemotePathExists = true; });
        _directories.Add("/data");

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");

        Assert.StartsWith("/data/D/Invoice/Others/", entry.FileLocation, StringComparison.Ordinal);
        Assert.Equal(["/data/D", "/data/D/Invoice", "/data/D/Invoice/Others"], _created);
    }

    [Fact]
    public async Task Directory_creation_can_be_disabled()
    {
        using var storage = Create(o => o.EnsureRemotePathExists = false);
        await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");
        Assert.Empty(_created);
        await _client.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_files_and_idempotent_deletes()
    {
        using var storage = Create();
        var missing = new FileEntry { FileLocation = "Files/nope.txt" };

        await Assert.ThrowsAsync<StorageNotFoundException>(() => storage.OpenReadAsync(missing));
        Assert.False(await storage.ExistsAsync(missing));
        await storage.DeleteAsync(missing);

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");
        await storage.DeleteAsync(entry);
        Assert.False(_files.ContainsKey(entry.FileLocation));
    }

    [Fact]
    public async Task Other_download_errors_propagate()
    {
        using var storage = Create();
        _client.DownloadFileAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SshException("broken pipe"));
        await Assert.ThrowsAsync<SshException>(() => storage.OpenReadAsync(new FileEntry { FileLocation = "x" }));
    }

    [Fact]
    public async Task Overwrite_writes_to_the_same_path_creating_its_directory()
    {
        using var storage = Create();
        var entry = new FileEntry { FileLocation = "Files/restored/deep/a.txt", FileExtension = ".txt" };

        await storage.OverwriteAsync(entry, new MemoryStream(TestData.Text("restored")));

        Assert.Equal(TestData.Text("restored"), _files["Files/restored/deep/a.txt"]);
        Assert.Equal(["Files/restored", "Files/restored/deep"], _created);
    }

    [Fact]
    public async Task Connects_lazily_and_maps_host_key_rejection_to_a_configuration_error()
    {
        using var storage = Create();
        _client.IsConnected.Returns(false);
        _client.ConnectAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new SshConnectionException("bad key", DisconnectReason.HostKeyNotVerifiable));

        var ex = await Assert.ThrowsAsync<StorageConfigurationException>(() => storage.ExistsAsync(new FileEntry { FileLocation = "x" }));
        Assert.Contains("HostKeyFingerprint", ex.Message, StringComparison.Ordinal);

        _client.ConnectAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new SshConnectionException("refused", DisconnectReason.ConnectionLost));
        await Assert.ThrowsAsync<SshConnectionException>(() => storage.ExistsAsync(new FileEntry { FileLocation = "x" }));
    }

    [Fact]
    public async Task Disposal()
    {
        var storage = Create();
        storage.Dispose();
        storage.Dispose();
        _client.Received(1).Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => storage.ExistsAsync(new FileEntry { FileLocation = "x" }));
    }
}

public class SftpSupportTests
{
    [Theory]
    [InlineData("SHA256:abc=", "abc", true)]
    [InlineData("sha256:abc", "abc==", true)]
    [InlineData("abc", "abd", false)]
    [InlineData(null, "abc", false)]
    [InlineData(" ", "abc", false)]
    public void Host_keys_must_match_the_pinned_fingerprint(string? pinned, string presented, bool trusted)
        => Assert.Equal(trusted, SftpClientFactory.IsTrusted(new SftpOptions { HostKeyFingerprint = pinned }, presented));

    [Fact]
    public void Accept_any_host_key_trusts_everything()
        => Assert.True(SftpClientFactory.IsTrusted(new SftpOptions { AcceptAnyHostKey = true }, "anything"));

    [Fact]
    public void Client_factory_builds_password_and_key_authentication()
    {
        using var password = SftpClientFactory.Create(new SftpOptions { Host = "h", Port = 2222, Username = "u", Password = "p", ConnectionTimeoutSeconds = 7 });
        Assert.Equal(("h", 2222, "u", TimeSpan.FromSeconds(7)), (password.ConnectionInfo.Host, password.ConnectionInfo.Port, password.ConnectionInfo.Username, password.ConnectionInfo.Timeout));
        Assert.IsType<PasswordAuthenticationMethod>(Assert.Single(password.ConnectionInfo.AuthenticationMethods));

        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        using var key = SftpClientFactory.Create(new SftpOptions { Host = "h", Username = "u", PrivateKey = pem, Password = "p" });
        Assert.Equal([typeof(PrivateKeyAuthenticationMethod), typeof(PasswordAuthenticationMethod)], key.ConnectionInfo.AuthenticationMethods.Select(m => m.GetType()));

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, pem);
            using var fromFile = SftpClientFactory.Create(new SftpOptions { Host = "h", Username = "u", PrivateKeyPath = path });
            Assert.IsType<PrivateKeyAuthenticationMethod>(Assert.Single(fromFile.ConnectionInfo.AuthenticationMethods));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("h", 22, "u", "p", null, "SHA256:x", false, 30, true)]
    [InlineData("h", 22, "u", null, "key", null, true, 30, true)]
    [InlineData("", 22, "u", "p", null, "x", false, 30, false)]
    [InlineData("h", 0, "u", "p", null, "x", false, 30, false)]
    [InlineData("h", 22, "", "p", null, "x", false, 30, false)]
    [InlineData("h", 22, "u", null, null, "x", false, 30, false)]
    [InlineData("h", 22, "u", "p", null, null, false, 30, false)]
    [InlineData("h", 22, "u", "p", null, "x", false, 0, false)]
    public void Options_are_validated(string host, int port, string user, string? password, string? key, string? fingerprint, bool acceptAny, int timeout, bool valid)
    {
        var options = new SftpOptions
        {
            Host = host, Port = port, Username = user, Password = password, PrivateKey = key,
            HostKeyFingerprint = fingerprint, AcceptAnyHostKey = acceptAny, ConnectionTimeoutSeconds = timeout,
        };
        Assert.Equal(valid, System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new(options), [], validateAllProperties: true));
    }

    [Fact]
    public async Task Health_check_is_unhealthy_when_the_server_is_unreachable()
    {
        var check = new SftpHealthCheck(MsOptions.Create(new SftpOptions { Host = "127.0.0.1", Port = 1, Username = "u", Password = "p", AcceptAnyHostKey = true, ConnectionTimeoutSeconds = 5 }));
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("sftp", check, HealthStatus.Unhealthy, null) });
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Throws<ArgumentNullException>(() => new SftpHealthCheck(null!));
    }

    [Fact]
    public void Registration_binds_Storage_Sftp_and_validates_on_resolve()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Sftp:Host"] = "sftp.local",
            ["Storage:Sftp:Username"] = "u",
            ["Storage:Sftp:Password"] = "p",
            ["Storage:Sftp:HostKeyFingerprint"] = "SHA256:abc",
        }).Build();
        var services = new ServiceCollection();
        services.AddKitStackStorage(config).AddSftp(o => o.RemotePath = "/srv");
        using var sp = services.BuildServiceProvider();
        Assert.Equal("/srv", sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SftpOptions>>().Value.RemotePath);
        Assert.IsType<SftpFileStorageManager>(sp.GetRequiredService<IFileStorageManager>());

        var invalid = new ServiceCollection();
        invalid.AddKitStackStorage(new ConfigurationBuilder().Build()).AddSftp();
        using var invalidSp = invalid.BuildServiceProvider();
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => invalidSp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SftpOptions>>().Value);
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => new ServiceCollection().AddSftpStorageManager(new SftpOptions()));
        Assert.NotNull(new ServiceCollection().AddSftpStorageManager(new SftpOptions { Host = "h", Username = "u", Password = "p", AcceptAnyHostKey = true }));
    }
}
