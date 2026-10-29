using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Fakes.Extensions;
using KitStack.Storage.Mongo;
using KitStack.Storage.Mongo.Documents;
using KitStack.Storage.Mongo.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.Tests.Mongo;

public sealed class MongoRegistrationTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    [Fact]
    public async Task Configuration_from_Storage_Database_reaches_the_server()
    {
        var database = "kitstack_cfg_" + Guid.NewGuid().ToString("N");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Database:ConnectionString"] = mongo.ConnectionString,
            ["Storage:Database:DatabaseName"] = database,
            ["Storage:Database:CollectionName"] = "files",
        }).Build();
        var services = new ServiceCollection();
        services.AddKitStackStorage(config).AddFake().AddMongoStore(trackUploads: false);
        await using var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<IFileEntryStore>();
        Assert.Same(sp.GetRequiredService<MongoFileEntryStore>(), store);
        Assert.Equal("files", ((MongoFileEntryStore)store).Collection.CollectionNamespace.CollectionName);
        Assert.Equal(database, ((MongoFileEntryStore)store).Collection.Database.DatabaseNamespace.DatabaseName);
        Assert.IsNotType<KitStack.Abstractions.Services.FileEntryTrackingStorageManager>(sp.GetRequiredService<IFileStorageManager>());

        var entry = new FileEntry { FileLocation = "a", LastAccessedTime = null };
        await store.AddAsync(entry);
        Assert.Null((await store.FindAsync(entry.Id))!.LastAccessedTime);
    }

    [Fact]
    public void Missing_connection_settings_fail_fast()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKitStackStorage().AddFake().AddMongoStore());
        Assert.Throws<ArgumentNullException>(() => ((KitStackStorageBuilder)null!).AddMongoStore());
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddKitStackStorage().AddMongoStore((Func<IServiceProvider, MongoDB.Driver.IMongoDatabase>)null!));
        Assert.Throws<ArgumentNullException>(() => new MongoFileEntryStore(null!));
        Assert.Throws<ArgumentException>(() => new MongoFileEntryStore(mongo.NewDatabase(), " "));
    }

    [Fact]
    public async Task Store_handles_updates_unknown_ids_and_argument_checks()
    {
        var store = new MongoFileEntryStore(mongo.NewDatabase());
        var entry = new FileEntry { FileLocation = "x/y.txt", Metadata = null };
        await store.AddAsync(entry);

        entry.Checksum = "c";
        entry.Encrypted = true;
        entry.LastAccessedTime = DateTimeOffset.UtcNow;
        entry.Metadata = new Dictionary<string, string> { ["a"] = "b" };
        await store.UpdateAsync(entry);
        var updated = await store.FindAsync(entry.Id);
        Assert.Equal(("c", true, "b"), (updated!.Checksum, updated.Encrypted, updated.Metadata!["a"]));

        var unknown = Guid.NewGuid();
        await store.LinkAsync(unknown, "A", "1");
        await store.UnlinkAsync(unknown, "A", "1");
        await store.MarkDeletedAsync(unknown);
        await store.UpdateAsync(new FileEntry { Id = unknown, Metadata = null });
        Assert.Null(await store.FindAsync(unknown));

        await store.MarkDeletedAsync(entry.Id);
        Assert.Null(await store.FindByLocationAsync("x/y.txt"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AddAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.UpdateAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => store.FindByLocationAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LinkAsync(entry.Id, "", "1"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UnlinkAsync(entry.Id, "A", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListByEntityAsync("A", " "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListOrphansAsync(DateTimeOffset.UtcNow, 0));
    }

    [Fact]
    public void Document_mapping_round_trips()
    {
        var entry = new FileEntry { UploadedTime = DateTimeOffset.UtcNow, LastAccessedTime = DateTimeOffset.UtcNow, Metadata = null, RelatedEntities = null };
        var back = FileEntryDocument.From(entry).ToFileEntry();
        Assert.Equal(entry.Id, back.Id);
        Assert.Empty(back.Metadata!);
        Assert.Empty(back.RelatedEntities!);
        Assert.Throws<ArgumentNullException>(() => FileEntryDocument.From(null!));
    }
}
