using System.Runtime.InteropServices;
using EphemeralMongo;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Services;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Extensions;
using KitStack.Storage.Mongo;
using KitStack.Storage.Mongo.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace KitStack.Storage.Tests.Mongo;

/// <summary>One mongod for all Mongo tests; each test uses its own database.</summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private IMongoRunner? _runner;

    public IMongoClient Client { get; private set; } = null!;

    public string ConnectionString => _runner!.ConnectionString;

    public async Task InitializeAsync()
    {
        var options = new MongoRunnerOptions { UseSingleNodeReplicaSet = false };
        // On linux-x64 use the binary bundled in the EphemeralMongo8 runtime package; elsewhere let EphemeralMongo download one.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64
            && AppContext.GetData("KitStack.Tests.MongoBinaryDirectory") is string directory && Directory.Exists(directory))
            options.BinaryDirectory = directory;

        _runner = await MongoRunner.RunAsync(options);
        Client = new MongoClient(_runner.ConnectionString);
    }

    public IMongoDatabase NewDatabase() => Client.GetDatabase("kitstack_" + Guid.NewGuid().ToString("N"));

    public Task DisposeAsync()
    {
        _runner?.Dispose();
        return Task.CompletedTask;
    }
}

public sealed class MongoFileEntryStoreTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static FileEntry Entry(DateTimeOffset? uploaded = null, string? parent = null) => new()
    {
        FileName = "a.pdf",
        FileLocation = "Docs/Invoice/Others/" + Guid.NewGuid().ToString("N") + "-a.pdf",
        ContentType = "application/pdf",
        Checksum = new string('a', 64),
        Size = 10,
        UploadedTime = uploaded ?? DateTimeOffset.UtcNow,
        VariantType = parent is null ? "original" : "thumbnail",
        Metadata = parent is null ? new Dictionary<string, string> { ["key.with.dots"] = "v" } : new Dictionary<string, string> { ["OriginalId"] = parent },
    };

    [Fact]
    public async Task Round_trips_entries_and_ignores_duplicates()
    {
        var store = new MongoFileEntryStore(mongo.NewDatabase());
        var entry = Entry();

        await store.AddAsync(entry);
        await store.AddAsync(entry);

        var found = await store.FindAsync(entry.Id);
        Assert.NotNull(found);
        Assert.Equal(entry.FileLocation, found.FileLocation);
        Assert.Equal(entry.Checksum, found.Checksum);
        Assert.Equal("v", found.Metadata!["key.with.dots"]);
        Assert.Equal(entry.UploadedTime.ToUnixTimeMilliseconds(), found.UploadedTime.ToUnixTimeMilliseconds());
        Assert.Equal(entry.Id, (await store.FindByLocationAsync(entry.FileLocation))!.Id);
        Assert.Equal(1, await store.Collection.CountDocumentsAsync(FilterDefinition<KitStack.Storage.Mongo.Documents.FileEntryDocument>.Empty));
    }

    [Fact]
    public async Task Links_are_added_once_released_and_reactivated()
    {
        var store = new MongoFileEntryStore(mongo.NewDatabase());
        var entry = Entry();
        await store.AddAsync(entry);

        await store.LinkAsync(entry.Id, "Letter", "7", "scan");
        await store.LinkAsync(entry.Id, "Letter", "7");
        var linked = Assert.Single(await store.ListByEntityAsync("Letter", "7"));
        Assert.Single(linked.RelatedEntities!);

        await store.UnlinkAsync(entry.Id, "Letter", "7");
        Assert.Empty(await store.ListByEntityAsync("Letter", "7"));
        Assert.Single(await store.ListByEntityAsync("Letter", "7", includeUnused: true));

        await store.LinkAsync(entry.Id, "Letter", "7");
        Assert.Single((await store.ListByEntityAsync("Letter", "7")).Single().RelatedEntities!);
    }

    [Fact]
    public async Task Orphans_exclude_linked_recent_deleted_and_variant_entries()
    {
        var store = new MongoFileEntryStore(mongo.NewDatabase());
        var old = DateTimeOffset.UtcNow.AddDays(-3);
        var orphan = Entry(old);
        var linked = Entry(old);
        var recent = Entry();
        var deleted = Entry(old);
        var variant = Entry(old, parent: orphan.Id.ToString("N"));
        foreach (var e in new[] { orphan, linked, recent, deleted, variant })
            await store.AddAsync(e);
        await store.LinkAsync(linked.Id, "Contract", "1");
        await store.MarkDeletedAsync(deleted.Id);

        var orphans = await store.ListOrphansAsync(DateTimeOffset.UtcNow.AddDays(-1), 10);

        Assert.Equal([orphan.Id], orphans.Select(o => o.Id));

        await store.MarkDeletedAsync(orphan.Id);
        Assert.True((await store.FindAsync(variant.Id))!.IsDeleted);
    }

    [Fact]
    public async Task Tracked_uploads_overwrites_and_sweeps_work_end_to_end()
    {
        var database = mongo.NewDatabase();
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddMongoStore(_ => database);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageManager>();
        var store = scope.ServiceProvider.GetRequiredService<IFileEntryStore>();

        var kept = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("keep")), "keep.txt", null, "Billing");
        var dropped = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("drop")), "drop.txt", null, "Billing");
        await store.LinkAsync(kept.Id, nameof(Invoice), "1");

        await storage.OverwriteAsync(kept, new MemoryStream(TestData.Text("kept, edited")));
        Assert.Equal(TestData.Sha256(TestData.Text("kept, edited")), (await store.FindAsync(kept.Id))!.Checksum);

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<FileOrphanSweeper>().SweepAsync(TimeSpan.Zero));
        var fake = sp.GetRequiredService<IFakeFileStore>();
        Assert.False(fake.TryGetFile(dropped.FileLocation, out _));
        Assert.True(fake.TryGetFile(kept.FileLocation, out _));
        Assert.True((await store.FindAsync(dropped.Id))!.IsDeleted);
    }
}
