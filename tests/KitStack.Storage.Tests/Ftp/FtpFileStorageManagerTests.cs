using System.ComponentModel.DataAnnotations;
using FluentFTP;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Storage.Ftp.Extensions;
using KitStack.Storage.Ftp.HealthChecks;
using KitStack.Storage.Ftp.Options;
using KitStack.Storage.Ftp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests.Ftp;

public class FtpFileStorageManagerTests
{
    private readonly IAsyncFtpClient _client = Substitute.For<IAsyncFtpClient>();
    private readonly Dictionary<string, byte[]> _files = [];
    private readonly List<(string Path, bool CreateDir, FtpRemoteExists Mode)> _uploads = [];

    public FtpFileStorageManagerTests()
    {
        _client.UploadStream(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<FtpRemoteExists>(), Arg.Any<bool>(), Arg.Any<IProgress<FtpProgress>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (ci.ArgAt<Stream>(0) is not { } stream)
                    return FtpStatus.Success; // re-configuration call
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                _files[ci.ArgAt<string>(1)] = ms.ToArray();
                _uploads.Add((ci.ArgAt<string>(1), ci.ArgAt<bool>(3), ci.ArgAt<FtpRemoteExists>(2)));
                return FtpStatus.Success;
            });
        _client.FileExists(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci => _files.ContainsKey(ci.ArgAt<string>(0)));
        _client.DownloadStream(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IProgress<FtpProgress>>(), Arg.Any<CancellationToken>(), Arg.Any<long>())
            .Returns(ci =>
            {
                if (ci.ArgAt<Stream>(0) is not { } stream)
                    return true; // re-configuration call
                stream.Write(_files[ci.ArgAt<string>(1)]);
                return true;
            });
        _client.DeleteFile(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _files.Remove(ci.ArgAt<string>(0));
            return Task.CompletedTask;
        });
    }

    private FtpFileStorageManager Create(Action<FtpOptions>? configure = null, StorageOptions? storage = null)
    {
        var options = new FtpOptions { Host = "ftp.local" };
        configure?.Invoke(options);
        return new FtpFileStorageManager(MsOptions.Create(options), _client, storage is null ? null : MsOptions.Create(storage));
    }

    [Fact]
    public async Task Uploads_under_the_remote_path_and_reads_back()
    {
        await using var storage = Create();
        var pdf = TestData.Pdf();

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "a.pdf", null, "Legal");

        var (path, createDir, mode) = Assert.Single(_uploads);
        Assert.Equal(FtpRemoteExists.Overwrite, mode);
        Assert.StartsWith("Files/Legal/Invoice/Others/", path, StringComparison.Ordinal);
        Assert.True(createDir);
        Assert.Equal(path, entry.FileLocation);
        Assert.Equal("Ftp", entry.StorageProvider);
        Assert.True(await storage.ExistsAsync(entry));

        await using var stream = await storage.OpenReadAsync(entry);
        Assert.True(stream.CanSeek);
        Assert.Equal(pdf, await TestData.ReadAllAsync(stream));
    }

    [Fact]
    public async Task Connects_lazily_and_only_when_disconnected()
    {
        await using var storage = Create();
        _client.IsConnected.Returns(false, true);

        await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");
        await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "b.txt", null, "D");

        await _client.Received(1).Connect(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Custom_remote_path_and_directory_creation_setting()
    {
        await using var storage = Create(o => { o.RemotePath = "/srv/uploads/"; o.EnsureRemotePathExists = false; });

        await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");

        Assert.StartsWith("srv/uploads/D/Invoice/Others/", _uploads[0].Path, StringComparison.Ordinal);
        Assert.False(_uploads[0].CreateDir);
    }

    [Fact]
    public async Task Failed_transfers_raise_storage_exceptions()
    {
        await using var storage = Create();
        _client.UploadStream(default!, default!, default, default, default, default).ReturnsForAnyArgs(FtpStatus.Failed);
        await Assert.ThrowsAsync<StorageException>(() => storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D"));

        _files["x.txt"] = [1];
        _client.DownloadStream(default!, default!, default, default, default, default).ReturnsForAnyArgs(false);
        await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync(new FileEntry { FileLocation = "x.txt" }));
    }

    [Fact]
    public async Task Missing_files_are_not_found_and_deletes_are_idempotent()
    {
        await using var storage = Create();
        var missing = new FileEntry { FileLocation = "Files/nope.txt" };

        await Assert.ThrowsAsync<StorageNotFoundException>(() => storage.OpenReadAsync(missing));
        Assert.False(await storage.ExistsAsync(missing));
        await storage.DeleteAsync(missing);
        await _client.DidNotReceive().DeleteFile(Arg.Any<string>(), Arg.Any<CancellationToken>());

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");
        await storage.DeleteAsync(entry);
        await _client.Received(1).DeleteFile(entry.FileLocation, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Overwrite_and_encryption()
    {
        await using var storage = Create(storage: new StorageOptions { MasterEncryptionKey = TestData.NewKey() });
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("v1")), "a.txt", null, "D");

        await storage.OverwriteAsync(entry, new MemoryStream(TestData.Text("v2")));

        Assert.Equal(entry.FileLocation, _uploads[1].Path);
        Assert.StartsWith("KSENC", System.Text.Encoding.ASCII.GetString(_files[entry.FileLocation], 0, 5), StringComparison.Ordinal);
        Assert.Equal(TestData.Text("v2"), await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
        Assert.Null(await storage.GetUrlAsync(entry));
    }

    [Fact]
    public async Task Concurrent_operations_are_serialised_on_the_shared_connection()
    {
        await using var storage = Create();
        var active = 0;
        var maxActive = 0;
        _client.FileExists(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
            return false;
        });

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => storage.ExistsAsync(new FileEntry { FileLocation = $"f{i}" })));

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Disposal_releases_the_client_once()
    {
        var storage = Create();
        await storage.DisposeAsync();
        await storage.DisposeAsync();
        storage.Dispose();
        await _client.Received(1).DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => storage.ExistsAsync(new FileEntry { FileLocation = "x" }));

        var sync = Create();
        sync.Dispose();
        _client.Received(1).Dispose();
    }
}

public class FtpSupportTests
{
    [Fact]
    public void Client_factory_applies_connection_and_tls_settings()
    {
        using var anonymous = FtpClientFactory.Create(new FtpOptions { Host = "h" });
        Assert.Equal("anonymous", anonymous.Credentials.UserName);

        using var plain = FtpClientFactory.Create(new FtpOptions { Host = "h", Port = 2121, Username = "u", Password = "p" });
        Assert.Equal(("h", 2121, FtpEncryptionMode.None), (plain.Host, plain.Port, plain.Config.EncryptionMode));
        Assert.Equal("u", plain.Credentials.UserName);

        using var tls = FtpClientFactory.Create(new FtpOptions { Host = "h", UseSsl = true, ValidateAnyCertificate = true });
        Assert.Equal(FtpEncryptionMode.Explicit, tls.Config.EncryptionMode);
        Assert.True(tls.Config.DataConnectionEncryption);
        Assert.True(tls.Config.ValidateAnyCertificate);
    }

    [Theory]
    [InlineData("h", 21, null, null, true)]
    [InlineData("h", 21, "u", "p", true)]
    [InlineData("", 21, null, null, false)]
    [InlineData("h", 0, null, null, false)]
    [InlineData("h", 70000, null, null, false)]
    [InlineData("h", 21, "u", null, false)]
    public void Options_are_validated(string host, int port, string? user, string? password, bool valid)
    {
        var options = new FtpOptions { Host = host, Port = port, Username = user, Password = password };
        Assert.Equal(valid, Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true));
    }

    [Fact]
    public async Task Health_check_is_unhealthy_when_the_server_is_unreachable()
    {
        var check = new FtpHealthCheck(MsOptions.Create(new FtpOptions { Host = "127.0.0.1", Port = 1 }));
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("ftp", check, HealthStatus.Unhealthy, null) });
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
        Assert.Throws<ArgumentNullException>(() => new FtpHealthCheck(null!));
    }

    [Fact]
    public void Registration_binds_Storage_Ftp_and_validates_on_resolve()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Ftp:Host"] = "ftp.local", ["Storage:Ftp:UseSsl"] = "true" }).Build();
        var services = new ServiceCollection();
        services.AddKitStackStorage(config).AddFtp(o => o.RemotePath = "x");
        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FtpOptions>>().Value;
        Assert.Equal(("ftp.local", true, "x"), (options.Host, options.UseSsl, options.RemotePath));
        Assert.IsType<FtpFileStorageManager>(sp.GetRequiredService<IFileStorageManager>());

        var invalid = new ServiceCollection();
        invalid.AddKitStackStorage(new ConfigurationBuilder().Build()).AddFtp();
        using var invalidSp = invalid.BuildServiceProvider();
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => invalidSp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FtpOptions>>().Value);

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => new ServiceCollection().AddFtpStorageManager(new FtpOptions()));
        Assert.NotNull(new ServiceCollection().AddFtpStorageManager(new FtpOptions { Host = "h" }));
    }
}
