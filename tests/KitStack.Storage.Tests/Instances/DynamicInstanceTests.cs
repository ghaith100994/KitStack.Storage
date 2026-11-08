using KitStack.Storage.Abstractions.DependencyInjection;
using KitStack.Storage.Abstractions.Exceptions;
using KitStack.Storage.Abstractions.Extensions;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using KitStack.Storage.Abstractions.Options;
using KitStack.Storage.Fakes.Extensions;
using KitStack.Storage.Local.Extensions;
using KitStack.Storage.S3.Extensions;
using KitStack.Storage.S3.Services;
using KitStack.Storage.Sftp.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace KitStack.Storage.Tests.Instances;

public sealed class DynamicInstanceTests : IDisposable
{
    private const string Secret = "SUPER-SECRET-VALUE-6f1c";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kitstack-dynamic-" + Guid.NewGuid().ToString("N"));
    private readonly TestInstanceSource _source = new();
    private readonly CountingProviderFactory _counting = new();
    private readonly FakeS3 _s3 = new();
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ServiceProvider Build(TimeSpan? refresh = null, Action<KitStackStorageBuilder>? configure = null, Dictionary<string, string?>? configuration = null)
    {
        var services = new ServiceCollection().AddLogging(l => l.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build());
        services.AddSingleton<IStorageProviderFactory>(_counting);
        services.AddSingleton<IS3ClientFactory>(_s3);
        var builder = services.AddKitStackStorage()
            .AddLocalProvider()
            .AddFakeProvider()
            .AddS3Provider()
            .AddSftpProvider()
            .AddStorageInstanceSource(_source, o => o.RefreshInterval = refresh ?? TimeSpan.FromMinutes(5));
        configure?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Builds_on_first_use_caches_rebuilds_on_version_change_and_invalidates()
    {
        _source.Set(TestInstanceSource.Definition("FMS-Vehicle-Images", CountingProviderFactory.Type, "1"));
        await using var sp = Build(refresh: TimeSpan.FromMinutes(5));
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        Assert.Empty(_counting.Created);

        var first = await provider.GetAsync("fms-vehicle-images");
        Assert.Single(_counting.Created);
        Assert.Contains("FMS-Vehicle-Images", provider.Names);
        Assert.Equal("FMS-Vehicle-Images", first.StorageName);

        // Cache hit: the source is not asked again within the refresh interval.
        var finds = _source.Finds;
        Assert.Same(first, provider.Get("FMS-Vehicle-Images"));
        Assert.Equal(finds, _source.Finds);
        Assert.Single(_counting.Created);

        // A new version is picked up after Invalidate (or the refresh interval) and the old build is disposed.
        _source.Set(TestInstanceSource.Definition("FMS-Vehicle-Images", CountingProviderFactory.Type, "2"));
        Assert.Same(first, provider.Get("FMS-Vehicle-Images"));
        provider.Invalidate("fms-vehicle-images");
        var second = await provider.GetAsync("FMS-Vehicle-Images");
        Assert.NotSame(first, second);
        Assert.Equal(2, _counting.Created.Count);
        var builds = _counting.Created.OrderBy(m => m.Disposals).ToList();
        Assert.Equal(1, _counting.Created.Sum(m => m.Disposals));

        // A disabled/removed definition is no longer resolvable.
        _source.Remove("FMS-Vehicle-Images");
        provider.InvalidateAll();
        Assert.Throws<StorageInstanceNotFoundException>(() => provider.Get("FMS-Vehicle-Images"));
        Assert.All(_counting.Created, m => Assert.Equal(1, m.Disposals));
        Assert.Contains("Built storage instance FMS-Vehicle-Images", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_changes_are_detected_on_refresh_and_unchanged_versions_are_reused()
    {
        _source.Set(TestInstanceSource.Definition("docs", CountingProviderFactory.Type, "a"));
        await using var sp = Build(refresh: TimeSpan.Zero);
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        var first = await provider.GetAsync("docs");
        Assert.Same(first, await provider.GetAsync("docs"));
        Assert.Single(_counting.Created);

        _source.Set(TestInstanceSource.Definition("docs", CountingProviderFactory.Type, "b"));
        var rebuilt = await provider.GetAsync("docs");
        Assert.NotSame(first, rebuilt);
        Assert.Equal(2, _counting.Created.Count);
        Assert.Contains("Rebuilt storage instance docs", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaced_S3_clients_are_disposed_exactly_once_and_not_while_a_read_stream_is_open()
    {
        _source.Set(TestInstanceSource.Definition("archive", "S3", "1", ("MainTarget:BucketName", "archive-v1")));
        await using var sp = Build();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        var manager = await provider.GetAsync("archive");
        var entry = await manager.CreateAsync(new StorageWriteOptions { FileName = "a.pdf", Folder = "archiving" }, new MemoryStream(TestData.Pdf()));
        var oldClient = _s3.Clients.Single().Client;

        var reading = await manager.OpenReadAsync(entry);
        var firstBytes = new byte[4];
        await reading.ReadExactlyAsync(firstBytes);

        _source.Set(TestInstanceSource.Definition("archive", "S3", "2", ("MainTarget:BucketName", "archive-v2")));
        provider.Invalidate("archive");
        var current = await provider.GetAsync("archive");
        Assert.NotSame(manager, current);
        oldClient.DidNotReceive().Dispose();

        var rest = await TestData.ReadAllAsync(reading);
        Assert.Equal(TestData.Pdf(), firstBytes.Concat(rest).ToArray());
        oldClient.Received(1).Dispose();

        // The stale manager reference keeps working against the current build (new bucket), without disposing again.
        Assert.False(await manager.ExistsAsync(entry));
        provider.Invalidate("archive");
        await provider.GetAsync("archive");
        oldClient.Received(1).Dispose();
        Assert.Equal(new[] { "archive-v1", "archive-v2" }, _s3.Clients.Select(c => c.Target.BucketName).Order());
    }

    [Fact]
    public async Task Concurrent_first_use_builds_once_and_operations_survive_rebuilds()
    {
        _source.Delay = TimeSpan.FromMilliseconds(20);
        _source.Set(TestInstanceSource.Definition("busy", CountingProviderFactory.Type, "0"));
        await using var sp = Build(refresh: TimeSpan.Zero);
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        var resolved = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => provider.GetAsync("busy").AsTask())));
        Assert.Single(_counting.Created);
        Assert.Single(resolved.Distinct());

        _source.Delay = TimeSpan.Zero;
        using var stop = new CancellationTokenSource();
        var version = 0;
        var churn = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _source.Set(TestInstanceSource.Definition("busy", CountingProviderFactory.Type, (++version).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                provider.Invalidate("busy");
                await Task.Delay(1);
            }
        });

        var workers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            for (var n = 0; n < 40; n++)
            {
                var storage = await provider.GetAsync("busy");
                var entry = await storage.CreateAsync(new StorageWriteOptions { FileName = $"{i}-{n}.txt", Folder = "w" }, new MemoryStream(TestData.Text()));
                // Reads through a possibly-retired build must still succeed while the stream is open.
                var stream = await resolved[0].OpenReadAsync(entry).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
                if (stream is not null)
                    await stream.DisposeAsync();
            }
        })).ToList();

        await Task.WhenAll(workers);
        await stop.CancelAsync();
        await churn;
        var current = (await provider.GetAsync("busy")).Unwrap<DisposableInMemoryManager>();

        // Every replaced build was disposed exactly once; only the current one is alive.
        var builds = _counting.Created.ToList();
        Assert.True(builds.Count > 1);
        Assert.All(builds, b => Assert.InRange(b.Disposals, 0, 1));
        Assert.Equal(current, builds.Single(b => b.Disposals == 0));
    }

    [Fact]
    public async Task Test_connection_validates_and_probes_without_caching()
    {
        await using var sp = Build();
        var validator = sp.GetRequiredService<IStorageInstanceValidator>();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var path = Path.Combine(_root, "probe");

        var ok = await validator.TestAsync(TestInstanceSource.Definition("new-node", "Local", "1", ("Path", path)));
        Assert.True(ok.Succeeded, string.Join(", ", ok.Errors) + ok.Description);
        Assert.Equal(HealthStatus.Healthy, ok.Status);
        Assert.DoesNotContain("new-node", provider.Names);
        Assert.Equal(0, _source.Finds);

        var counting = await validator.TestAsync(TestInstanceSource.Definition("c", CountingProviderFactory.Type, "1"));
        Assert.True(counting.Succeeded);
        Assert.Empty(_counting.Created);

        var unknown = await validator.TestAsync(TestInstanceSource.Definition("x", "Dropbox", "1"));
        Assert.False(unknown.Succeeded);
        Assert.Contains("Unknown provider type 'Dropbox'", unknown.Errors.Single(), StringComparison.Ordinal);
        Assert.Null(unknown.Status);
    }

    [Fact]
    public async Task Invalid_options_produce_validation_errors_without_secrets()
    {
        await using var sp = Build(configuration: new() { ["StorageSecrets:s3-key"] = Secret, ["StorageSecrets:sftp-port"] = Secret });
        var validator = sp.GetRequiredService<IStorageInstanceValidator>();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        // Access key without its secret: the S3 options validation fails.
        var s3 = new StorageInstanceDefinition("hrm", "S3")
        {
            Settings = StorageInstanceDefinition.CreateSettings([new("MainTarget:BucketName", "erp-private")]),
            SecretReferences = new Dictionary<string, string> { [StorageSecretFields.S3AccessKeyId] = "s3-key" },
        };
        var s3Result = await validator.TestAsync(s3);
        Assert.Contains(s3Result.Errors, e => e.Contains("AccessKeyID", StringComparison.Ordinal));

        // A secret that cannot be bound (the binder quotes the value): redacted.
        var sftp = new StorageInstanceDefinition("ftp-node", "Sftp")
        {
            Settings = StorageInstanceDefinition.CreateSettings([new("Host", "h"), new("Username", "u"), new("Password", "inline-pass-XYZ"), new("AcceptAnyHostKey", "true")]),
            SecretReferences = new Dictionary<string, string> { ["Port"] = "sftp-port" },
        };
        var sftpResult = await validator.TestAsync(sftp);
        Assert.NotEmpty(sftpResult.Errors);

        var missing = await validator.TestAsync(new StorageInstanceDefinition("m", "S3")
        {
            SecretReferences = new Dictionary<string, string> { [StorageSecretFields.S3SecretAccessKey] = "does-not-exist" },
        });
        Assert.Equal("Secret reference 'does-not-exist' for 'SecretAccessKey' could not be resolved.", missing.Errors.Single());

        _source.Set(sftp);
        var thrown = await Assert.ThrowsAsync<StorageInstanceValidationException>(() => provider.GetAsync("ftp-node").AsTask());

        var texts = s3Result.Errors.Concat(sftpResult.Errors).Append(thrown.Message).Append(thrown.ToString()).Append(_logs.All).Append(sftp.ToString());
        Assert.All(texts, t =>
        {
            Assert.DoesNotContain(Secret, t, StringComparison.Ordinal);
            Assert.DoesNotContain("inline-pass-XYZ", t, StringComparison.Ordinal);
        });
        Assert.Contains("***", string.Join(' ', sftpResult.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_references_are_injected_at_build_time_and_never_logged()
    {
        var key = TestData.NewKey();
        await using var sp = Build(configuration: new()
        {
            ["StorageSecrets:hrm-access"] = "AKIA" + Secret,
            ["StorageSecrets:hrm-secret"] = Secret,
            ["StorageSecrets:hrm-encryption"] = key,
        });
        _source.Set(new StorageInstanceDefinition("HRM-Employee-Documents", "S3")
        {
            Version = "7",
            Settings = StorageInstanceDefinition.CreateSettings([new("MainTarget:BucketName", "erp-private"), new("MainTarget:Prefix", "hrm/employees/documents")]),
            SecretReferences = new Dictionary<string, string>
            {
                [StorageSecretFields.S3AccessKeyId] = "hrm-access",
                [StorageSecretFields.S3SecretAccessKey] = "hrm-secret",
                [StorageSecretFields.MasterEncryptionKey] = "hrm-encryption",
            },
        });
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        var storage = await provider.GetAsync("HRM-Employee-Documents");
        var entry = await storage.CreateAsync(new StorageWriteOptions { FileName = "cv.pdf", Folder = "cv" }, new MemoryStream(TestData.Pdf()));
        var (_, _, options) = _s3.Clients.Single();
        Assert.Equal(("AKIA" + Secret, Secret), (options.AccessKeyID, options.SecretAccessKey));
        Assert.True(entry.Encrypted);
        Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
        Assert.DoesNotContain(Secret, _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(key, _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, (await provider.GetAsync("HRM-Employee-Documents")).ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_failures_are_redacted_and_custom_secret_resolvers_are_used()
    {
        var resolver = Substitute.For<IStorageSecretResolver>();
        resolver.ResolveAsync("pk", Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>(Secret));
        await using var sp = Build(configure: b => b.AddStorageSecretResolver<FixedResolver>());
        _source.Set(new StorageInstanceDefinition("broken", "Sftp")
        {
            Settings = StorageInstanceDefinition.CreateSettings([new("Host", "h"), new("Username", "u"), new("AcceptAnyHostKey", "true")]),
            SecretReferences = new Dictionary<string, string> { [StorageSecretFields.SftpPrivateKey] = "pk" },
        });

        // SSH.NET rejects the "private key" while the instance is built; the message must not quote it.
        var ex = await Assert.ThrowsAnyAsync<StorageConfigurationException>(() => sp.GetRequiredService<IFileStorageManagerProvider>().GetAsync("broken").AsTask());
        Assert.DoesNotContain(FixedResolver.Value, ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
        Assert.Contains("broken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_probe_reaches_runtime_instances()
    {
        _source.Set(TestInstanceSource.Definition("runtime-local", "Local", "1", ("Path", Path.Combine(_root, "rt"))));
        await using var sp = Build();
        var probe = sp.GetRequiredService<IStorageHealthProbe>();
        Assert.Equal(HealthStatus.Healthy, (await probe.CheckAsync("runtime-local")).Status);
        Assert.DoesNotContain("runtime-local", sp.GetRequiredService<IFileStorageManagerProvider>().Names);
    }

    [Fact]
    public async Task Static_instances_win_over_the_source_and_tracking_covers_runtime_instances()
    {
        _source.Set(TestInstanceSource.Definition("shadowed", CountingProviderFactory.Type, "1"));
        _source.Set(TestInstanceSource.Definition("runtime", "Fake", "1"));
        await using var sp = Build(configure: b => b.AddFake("shadowed").AddInMemoryStore());
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        Assert.NotNull(provider.Get("shadowed").Unwrap<KitStack.Storage.Fakes.Services.InMemoryFileStorageManager>());
        Assert.Empty(_counting.Created);

        var entry = await provider.Get("runtime").CreateAsync(new StorageWriteOptions { FileName = "t.txt", Folder = "t" }, new MemoryStream(TestData.Text()));
        var stored = await sp.GetRequiredService<IFileEntryStore>().FindAsync(entry.Id);
        Assert.Equal("runtime", stored!.StorageName);
        await provider.Get(stored).DeleteAsync(stored);
        Assert.True((await sp.GetRequiredService<IFileEntryStore>().FindAsync(entry.Id))!.IsDeleted);
    }

    private sealed class FixedResolver : IStorageSecretResolver
    {
        public const string Value = "-----BEGIN NOT A KEY " + Secret + "-----";

        public ValueTask<string?> ResolveAsync(string reference, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(Value);
    }
}
