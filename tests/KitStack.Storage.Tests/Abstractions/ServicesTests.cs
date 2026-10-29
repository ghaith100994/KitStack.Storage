using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Hosting;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Services;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace KitStack.Storage.Tests.Abstractions;

public class FileEntryTrackingStorageManagerTests
{
    private readonly RecordingStorageManager _inner = new(images: new KitStack.Abstractions.Options.ImageProcessingOptions());
    private readonly IFileEntryStore _store = Substitute.For<IFileEntryStore>();
    private FileEntryTrackingStorageManager Tracked => new(_inner, _store);

    [Fact]
    public async Task Records_originals_and_variants()
    {
        var (primary, variants) = await Tracked.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");

        await _store.Received(1).AddAsync(primary, Arg.Any<CancellationToken>());
        foreach (var v in variants)
            await _store.Received(1).AddAsync(v, Arg.Any<CancellationToken>());
        Assert.Equal(2, variants.Count);
    }

    [Fact]
    public async Task Every_create_overload_records_the_entry()
    {
        var tracked = Tracked;
        var form = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(TestData.Text()), 0, TestData.Text().Length, "f", "a.txt") { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary() };

        await tracked.CreateAsync<Invoice>(form, "Docs");
        form = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(TestData.Text()), 0, TestData.Text().Length, "f", "a.txt") { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary() };
        await tracked.CreateAsync(new AttachableDocument(), form, "Docs");
        form = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(TestData.Text()), 0, TestData.Text().Length, "f", "a.txt") { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary() };
        await tracked.CreateWithVariantsAsync<Invoice>(form, "Docs");
        await tracked.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "b.txt", null, "Docs");

        await _store.Received(4).AddAsync(Arg.Any<IFileEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_store_removes_the_bytes_and_rethrows()
    {
        _store.AddAsync(Arg.Any<IFileEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Tracked.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs"));

        Assert.Empty(_inner.Files);
    }

    [Fact]
    public async Task Delete_overwrite_and_reads_reach_both_sides()
    {
        var tracked = Tracked;
        var entry = await tracked.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");

        Assert.True(await tracked.ExistsAsync(entry));
        Assert.Equal(TestData.Text(), await TestData.ReadAllAsync(await tracked.OpenReadAsync(entry)));
        Assert.Null(await tracked.GetUrlAsync(entry));
        await Assert.ThrowsAsync<NotSupportedException>(() => tracked.OverwriteAsync(entry, new MemoryStream()));
        await _store.DidNotReceive().UpdateAsync(Arg.Any<IFileEntry>(), Arg.Any<CancellationToken>());

        await tracked.DeleteAsync(entry);
        await _store.Received(1).MarkDeletedAsync(entry.Id, Arg.Any<CancellationToken>());
        Assert.Empty(_inner.Files);
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracked.DeleteAsync(null!));
    }

    [Fact]
    public async Task Overwrite_updates_the_record()
    {
        using var sp = new ServiceCollection().AddKitStackStorage().AddFake().Services.BuildServiceProvider();
        var tracked = new FileEntryTrackingStorageManager(sp.GetRequiredService<IFileStorageManager>(), _store);
        var entry = await tracked.CreateAsync<Invoice>(new MemoryStream(TestData.Text("a")), "a.txt", null, "Docs");

        await tracked.OverwriteAsync(entry, new MemoryStream(TestData.Text("b")));

        await _store.Received(1).UpdateAsync(Arg.Is<IFileEntry>(e => e.Checksum == TestData.Sha256(TestData.Text("b"))), Arg.Any<CancellationToken>());
    }
}

public class FileOrphanSweeperTests
{
    [Fact]
    public async Task Deletes_orphans_and_continues_after_a_failure()
    {
        var store = Substitute.For<IFileEntryStore>();
        var storage = Substitute.For<IFileStorageManager>();
        var ok1 = new FileEntry { FileLocation = "1" };
        var broken = new FileEntry { FileLocation = "2" };
        var ok2 = new FileEntry { FileLocation = "3" };
        store.ListOrphansAsync(Arg.Any<DateTimeOffset>(), 50, Arg.Any<CancellationToken>()).Returns([ok1, broken, ok2]);
        storage.DeleteAsync(broken, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("locked"));

        var deleted = await new FileOrphanSweeper(store, storage, NullLogger<FileOrphanSweeper>.Instance).SweepAsync(TimeSpan.FromHours(1), 50);

        Assert.Equal(2, deleted);
        await store.Received(1).MarkDeletedAsync(ok1.Id, Arg.Any<CancellationToken>());
        await store.Received(1).MarkDeletedAsync(ok2.Id, Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkDeletedAsync(broken.Id, Arg.Any<CancellationToken>());
        await store.Received(1).ListOrphansAsync(Arg.Is<DateTimeOffset>(d => d < DateTimeOffset.UtcNow.AddMinutes(-59)), 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validates_arguments_and_honours_cancellation()
    {
        var store = Substitute.For<IFileEntryStore>();
        store.ListOrphansAsync(default, default, default).ReturnsForAnyArgs([new FileEntry { FileLocation = "x" }]);
        var sweeper = new FileOrphanSweeper(store, Substitute.For<IFileStorageManager>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sweeper.SweepAsync(TimeSpan.FromSeconds(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sweeper.SweepAsync(TimeSpan.Zero, 0));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweeper.SweepAsync(TimeSpan.Zero, 10, cts.Token));
    }

    [Fact]
    public async Task Hosted_service_sweeps_on_start_and_survives_failures()
    {
        var store = Substitute.For<IFileEntryStore>();
        var calls = 0;
        var swept = new TaskCompletionSource();
        store.ListOrphansAsync(default, default, default).ReturnsForAnyArgs(_ =>
        {
            if (++calls >= 2)
                swept.TrySetResult();
            return calls == 1
                ? Task.FromException<IReadOnlyList<IFileEntry>>(new InvalidOperationException("first sweep fails"))
                : Task.FromResult<IReadOnlyList<IFileEntry>>([]);
        });

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddKitStackStorage().AddFake().AddOrphanSweeper(o => o.Interval = TimeSpan.FromMilliseconds(50));
        using var sp = services.BuildServiceProvider();
        var hosted = Assert.IsType<FileOrphanSweeperHostedService>(sp.GetServices<IHostedService>().Single());

        await hosted.StartAsync(CancellationToken.None);
        await swept.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await hosted.StopAsync(CancellationToken.None);

        Assert.True(calls >= 2);
    }

    [Fact]
    public void Sweeper_options_are_validated()
    {
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddOrphanSweeper(o => o.BatchSize = 0);
        using var sp = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<OrphanSweeperOptions>>().Value);
    }
}

public class BuilderTests
{
    private sealed class CustomManager : RecordingStorageManagerWrapper;

    [Fact]
    public void Tracking_wraps_instance_factory_and_type_registrations()
    {
        foreach (var register in new Action<IServiceCollection>[]
                 {
                     s => s.AddSingleton<IFileStorageManager>(new RecordingStorageManagerWrapper()),
                     s => s.AddSingleton<IFileStorageManager>(_ => new RecordingStorageManagerWrapper()),
                     s => s.AddSingleton<IFileStorageManager, CustomManager>(),
                 })
        {
            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<IFileEntryStore>());
            var builder = services.AddKitStackStorage();
            register(services);

            builder.AddFileEntryTracking().AddFileEntryTracking();

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            Assert.IsType<FileEntryTrackingStorageManager>(scope.ServiceProvider.GetRequiredService<IFileStorageManager>());
            Assert.Single(services, d => d.ServiceType == typeof(IFileStorageManager) && !d.IsKeyedService);
        }
    }

    [Fact]
    public void Builder_exposes_configuration_and_configure()
    {
        var builder = new ServiceCollection().AddKitStackStorage(o => o.Provider = "Fake").Configure(o => o.Upload.MaxBytes = 7);
        Assert.Null(builder.Provider);
        Assert.False(builder.IsProvider("Fake"));

        using var sp = builder.Services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<KitStack.Abstractions.Options.StorageOptions>>().Value;
        Assert.Equal(("Fake", 7L), (options.Provider, options.Upload.MaxBytes));
    }

    [Fact]
    public void Negative_size_limit_fails_validation()
    {
        using var sp = new ServiceCollection().AddKitStackStorage(o => o.Upload.MaxBytes = -1).Services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<KitStack.Abstractions.Options.StorageOptions>>().Value);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddKitStackStorage());
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddKitStackStorage((Microsoft.Extensions.Configuration.IConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => ((KitStackStorageBuilder)null!).AddFileEntryTracking());
        Assert.Throws<ArgumentNullException>(() => ((KitStackStorageBuilder)null!).AddFake());
    }

    [Fact]
    public void Fake_registration_overloads_share_one_instance()
    {
        foreach (var services in new[]
                 {
                     new ServiceCollection().AddInMemoryFakeStorage(),
                     new ServiceCollection().AddInMemoryFakeStorage(o => o.OperationDelayMs = 0),
                 })
        {
            using var sp = services.BuildServiceProvider();
            Assert.Same(sp.GetRequiredService<IFileStorageManager>(), sp.GetRequiredService<IFakeFileStore>());
        }
    }
}

/// <summary>Non-sealed wrapper so registration by type can be tested.</summary>
public class RecordingStorageManagerWrapper : IFileStorageManager
{
    private readonly RecordingStorageManager _inner = new();
    public Task<IFileEntry> CreateAsync<T>(Microsoft.AspNetCore.Http.IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class => _inner.CreateAsync<T>(file, category, cancellationToken);
    public Task<IFileEntry> CreateAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default) where T : class => _inner.CreateAsync<T>(content, fileName, contentType, category, cancellationToken);
    public Task<IFileEntry> CreateAsync<T>(T entity, Microsoft.AspNetCore.Http.IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class, IFileAttachable => _inner.CreateAsync(entity, file, category, cancellationToken);
    public Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(Microsoft.AspNetCore.Http.IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class => _inner.CreateWithVariantsAsync<T>(file, category, cancellationToken);
    public Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(Stream content, string fileName, string? contentType, string? category, CancellationToken cancellationToken = default) where T : class => _inner.CreateWithVariantsAsync<T>(content, fileName, contentType, category, cancellationToken);
    public Task<Stream> OpenReadAsync(IFileEntry entry, CancellationToken cancellationToken = default) => _inner.OpenReadAsync(entry, cancellationToken);
    public Task<bool> ExistsAsync(IFileEntry entry, CancellationToken cancellationToken = default) => _inner.ExistsAsync(entry, cancellationToken);
    public Task DeleteAsync(IFileEntry entry, CancellationToken cancellationToken = default) => _inner.DeleteAsync(entry, cancellationToken);
    public Task OverwriteAsync(IFileEntry entry, Stream content, CancellationToken cancellationToken = default) => _inner.OverwriteAsync(entry, content, cancellationToken);
    public Task<Uri?> GetUrlAsync(IFileEntry entry, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) => _inner.GetUrlAsync(entry, lifetime, cancellationToken);
}
