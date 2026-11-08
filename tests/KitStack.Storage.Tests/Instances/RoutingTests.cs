using System.Net;
using KitStack.Storage.Abstractions.DependencyInjection;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using KitStack.Storage.Abstractions.Services;
using KitStack.Storage.AspNetCore.Extensions;
using KitStack.Storage.Fakes.Contracts;
using KitStack.Storage.Fakes.Extensions;
using KitStack.Storage.EntityFrameworkCore.Extensions;
using KitStack.Storage.Tests.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KitStack.Storage.Tests.Instances;

public sealed class SweeperRoutingTests
{
    [Fact]
    public async Task Sweeper_deletes_each_orphan_through_its_owning_instance_and_isolates_failing_instances()
    {
        var source = new TestInstanceSource();
        source.Set(TestInstanceSource.Definition("broken", "NoSuchProvider", "1"));
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection().AddLogging(l => l.AddProvider(logs));
        services.AddKitStackStorage().AddFake().AddFake("a").AddFake("b").AddStorageInstanceSource(source).AddInMemoryStore();
        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var store = sp.GetRequiredService<IFileEntryStore>();

        StorageWriteOptions Doc() => new() { FileName = "x.txt", Folder = "tmp" };
        await provider.Get("default").CreateAsync(Doc(), new MemoryStream(TestData.Text()));
        var inA = await provider.Get("a").CreateAsync(Doc(), new MemoryStream(TestData.Text()));
        await provider.Get("b").CreateAsync(Doc(), new MemoryStream(TestData.Text()));
        var keep = await provider.Get("b").CreateAsync(Doc(), new MemoryStream(TestData.Text()));
        await store.LinkAsync(keep.Id, "Order", "1");
        var stranded = new FileEntry { StorageName = "broken", FileLocation = "tmp/old.txt", UploadedTime = DateTimeOffset.UtcNow.AddDays(-1) };
        await store.AddAsync(stranded);

        using (var scope = sp.CreateScope())
        {
            var sweeper = scope.ServiceProvider.GetRequiredService<FileOrphanSweeper>();
            Assert.Equal(1, await sweeper.SweepAsync(TimeSpan.Zero, 100, "a"));
            Assert.True((await store.FindAsync(inA.Id))!.IsDeleted);
            Assert.Single(sp.GetRequiredKeyedService<IFakeFileStore>("b").ListFiles().Where(f => f.FileEntry.Id != keep.Id));

            Assert.Equal(2, await sweeper.SweepAsync(TimeSpan.Zero, 100));
        }

        Assert.Empty(sp.GetRequiredService<IFakeFileStore>().ListFiles());
        Assert.Empty(sp.GetRequiredKeyedService<IFakeFileStore>("a").ListFiles());
        Assert.Equal(keep.FileLocation, sp.GetRequiredKeyedService<IFakeFileStore>("b").ListFiles().Single().FileEntry.FileLocation);
        Assert.False((await store.FindAsync(stranded.Id))!.IsDeleted);
        Assert.Contains("Skipping orphaned files of storage instance broken", logs.All, StringComparison.Ordinal);
    }
}

public sealed class EndpointRoutingTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddKitStackStorage().AddFake().AddFake("hrm").AddInMemoryStore();
        builder.Services.AddAuthorization();
        _app = builder.Build();
        _app.MapKitStackStorageFiles("/files").AllowAnonymous();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Files_are_served_from_the_instance_that_owns_them()
    {
        var provider = _app.Services.GetRequiredService<IFileStorageManagerProvider>();
        var hrm = await provider.Get("hrm").CreateAsync(new StorageWriteOptions { FileName = "cv.pdf", Folder = "hrm/cv" }, new MemoryStream(TestData.Pdf()));
        var main = await provider.Get("default").CreateAsync(new StorageWriteOptions { FileName = "a.txt", Folder = "misc" }, new MemoryStream(TestData.Text()));

        Assert.Equal(TestData.Pdf(), await _client.GetByteArrayAsync(new Uri($"/files/{hrm.Id}", UriKind.Relative)));
        Assert.Equal(TestData.Text(), await _client.GetByteArrayAsync(new Uri($"/files/{main.Id}", UriKind.Relative)));
        Assert.Equal(TestData.Pdf(), await _client.GetByteArrayAsync(new Uri($"/files/{hrm.FileLocation}", UriKind.Relative)));
        Assert.Equal(TestData.Pdf(), await _client.GetByteArrayAsync(new Uri($"/files/{hrm.FileLocation}?storage=hrm", UriKind.Relative)));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri($"/files/{hrm.FileLocation}?storage=default", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri($"/files/{hrm.FileLocation}?storage=unknown", UriKind.Relative))).StatusCode);
    }
}

public sealed class EfInstanceTests : IAsyncLifetime
{
    private readonly SqliteConnection _db = new("DataSource=:memory:");
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        await _db.OpenAsync();
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddFake("hrm").AddEntityFrameworkCoreStore<AppWithMetadataDbContext>();
        services.AddDbContext<AppWithMetadataDbContext>((sp, o) => o.UseSqlite(_db).AddKitStackFileReferences(sp));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppWithMetadataDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public void Schema_has_the_columns_described_in_the_migration_notes()
    {
        using var scope = _sp.CreateScope();
        var script = scope.ServiceProvider.GetRequiredService<AppWithMetadataDbContext>().Database.GenerateCreateScript();
        Assert.Contains("\"StorageName\" TEXT NULL", script, StringComparison.Ordinal);
        Assert.Contains("\"VersionNumber\" INTEGER NOT NULL DEFAULT 1", script, StringComparison.Ordinal);
        Assert.Contains("\"PreviousVersionId\" TEXT NULL", script, StringComparison.Ordinal);
        Assert.Contains("\"SupersededById\" TEXT NULL", script, StringComparison.Ordinal);
        Assert.Contains("\"Document_StorageName\" TEXT NULL", script, StringComparison.Ordinal);
        Assert.Contains("\"IX_FileEntries_StorageName_IsDeleted\"", script, StringComparison.Ordinal);
        Assert.Contains("\"IX_FileEntries_StorageName_Checksum\"", script, StringComparison.Ordinal);
        Assert.Contains("\"IX_FileEntries_SupersededById\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StorageName_is_persisted_and_routes_reads_with_null_meaning_default()
    {
        var provider = _sp.GetRequiredService<IFileStorageManagerProvider>();
        var store = _sp.GetRequiredService<IFileEntryStore>();

        var hrm = await provider.Get("hrm").CreateAsync(new StorageWriteOptions { FileName = "a.pdf", Folder = "hrm" }, new MemoryStream(TestData.Pdf()));
        using (var scope = _sp.CreateScope())
        {
            // Unkeyed (scoped, tracked) manager writes the default instance.
            var legacy = await scope.ServiceProvider.GetRequiredService<IFileStorageManager>().CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "b.txt", null, "Docs");
            Assert.Equal("default", (await store.FindAsync(legacy.Id))!.StorageName);

            // A 0.2 row has no StorageName: it is routed to default.
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE FileEntries SET StorageName = NULL WHERE FileName = 'b.txt'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            var old = await store.FindAsync(legacy.Id);
            Assert.Null(old!.StorageName);
            Assert.Equal(TestData.Text(), await TestData.ReadAllAsync(await provider.Get(old).OpenReadAsync(old)));

            // The unkeyed manager routes an entry of another instance to its owner.
            var reloaded = await store.FindAsync(hrm.Id);
            Assert.Equal("hrm", reloaded!.StorageName);
            Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await scope.ServiceProvider.GetRequiredService<IFileStorageManager>().OpenReadAsync(reloaded)));
        }

        // StoredFileRef carries the instance and OwnsStoredFile maps it.
        var contractId = Guid.NewGuid();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppWithMetadataDbContext>();
            db.Contracts.Add(new Contract { Id = contractId, Document = StoredFileRef.From(hrm) });
            await db.SaveChangesAsync();
        }

        using (var scope = _sp.CreateScope())
        {
            var contract = await scope.ServiceProvider.GetRequiredService<AppWithMetadataDbContext>().Contracts.SingleAsync(c => c.Id == contractId);
            Assert.Equal("hrm", contract.Document!.StorageName);
            Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await provider.Get(contract.Document).OpenReadAsync(contract.Document.ToFileEntry())));
        }

        Assert.Equal(hrm.Id, (await store.ListByEntityAsync(nameof(Contract), contractId.ToString())).Single().Id);
    }
}
