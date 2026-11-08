using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using KitStack.Storage.Fakes.Stores;
using KitStack.Storage.EntityFrameworkCore;
using KitStack.Storage.EntityFrameworkCore.Configuration;
using KitStack.Storage.EntityFrameworkCore.Stores;
using KitStack.Storage.Mongo;
using KitStack.Storage.Tests.Mongo;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace KitStack.Storage.Tests.Instances;

/// <summary>
/// The 0.3 store contract (instance names, query, usage, duplicates, relocation, versions), run against every store.
/// </summary>
public abstract class FileEntryStoreContractTests
{
    protected abstract IFileEntryStore Store { get; }

    private static FileEntry Entry(string? storage, string category = "Docs", string checksum = "c1", long size = 10, DateTimeOffset? uploaded = null,
        string contentType = "application/pdf", Guid? parent = null, string fileName = "a.pdf")
        => new()
        {
            FileName = fileName,
            FileLocation = $"{category}/{Guid.NewGuid():N}-{fileName}",
            Category = category,
            Checksum = checksum,
            Size = size,
            ContentType = contentType,
            UploadedTime = uploaded ?? DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(1, 1000)),
            StorageName = storage,
            VariantType = parent is null ? "original" : "thumbnail",
            Metadata = parent is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [FileEntryMetadataKeys.OriginalId] = parent.Value.ToString("N") },
        };

    private async Task<FileEntry> AddAsync(FileEntry entry)
    {
        await Store.AddAsync(entry);
        return entry;
    }

    [Fact]
    public async Task StorageName_and_version_fields_round_trip_and_null_means_default()
    {
        var legacy = await AddAsync(Entry(null));
        var hrm = await AddAsync(Entry("hrm-docs"));

        var found = await Store.FindAsync(hrm.Id);
        Assert.Equal("hrm-docs", found!.StorageName);
        Assert.Equal(1, found.VersionNumber);
        Assert.Null(found.PreviousVersionId);
        Assert.Null(found.SupersededById);
        Assert.Null((await Store.FindAsync(legacy.Id))!.StorageName);

        // null names are the default instance.
        Assert.NotNull(await Store.FindByLocationAsync(legacy.FileLocation, "default"));
        Assert.Null(await Store.FindByLocationAsync(legacy.FileLocation, "hrm-docs"));
        Assert.NotNull(await Store.FindByLocationAsync(hrm.FileLocation, "hrm-docs"));
        Assert.Null(await Store.FindByLocationAsync(hrm.FileLocation, "default"));

        var page = await Store.QueryAsync(new FileEntryQuery { StorageName = "default", Take = 1000 });
        Assert.Contains(page.Items, e => e.Id == legacy.Id);
        Assert.DoesNotContain(page.Items, e => e.Id == hrm.Id);
    }

    [Fact]
    public async Task FindMany_returns_known_ids()
    {
        var a = await AddAsync(Entry("x"));
        var b = await AddAsync(Entry("x"));
        var found = await Store.FindManyAsync([a.Id, b.Id, Guid.NewGuid(), a.Id]);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), found.Select(e => e.Id).Order());
    }

    [Fact]
    public async Task Query_filters_sorts_and_pages()
    {
        var storage = "q-" + Guid.NewGuid().ToString("N")[..8];
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var entries = new List<FileEntry>();
        for (var i = 0; i < 5; i++)
            entries.Add(await AddAsync(Entry(storage, "Invoices", checksum: "sum" + i, size: 100 + i, uploaded: t0.AddHours(i))));
        var image = await AddAsync(Entry(storage, "Photos", contentType: "image/png", size: 7, uploaded: t0.AddHours(10), fileName: "p.png"));
        var thumb = await AddAsync(Entry(storage, "Photos", contentType: "image/jpeg", size: 1, uploaded: t0.AddHours(11), parent: image.Id, fileName: "t.jpg"));
        await Store.LinkAsync(entries[0].Id, "Order", "42");
        var deleted = await AddAsync(Entry(storage, "Invoices", uploaded: t0.AddHours(20)));
        await Store.MarkDeletedAsync(deleted.Id);

        var all = await Store.QueryAsync(new FileEntryQuery { StorageName = storage, Take = 100 });
        Assert.Equal(7, all.TotalCount);
        Assert.Equal(thumb.Id, all.Items[0].Id); // newest first

        var paged = await Store.QueryAsync(new FileEntryQuery { StorageName = storage, Category = "Invoices", SortBy = FileEntrySortField.Size, Descending = false, Skip = 1, Take = 2 });
        Assert.Equal(5, paged.TotalCount);
        Assert.Equal(new[] { entries[1].Id, entries[2].Id }, paged.Items.Select(e => e.Id));

        Assert.Equal(2, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, ContentTypePrefix = "image/" })).TotalCount);
        Assert.Equal(1, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, ContentTypePrefix = "image/", OriginalsOnly = true })).TotalCount);
        Assert.Equal(thumb.Id, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, OriginalId = image.Id })).Items.Single().Id);
        Assert.Equal(entries[0].Id, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, EntityName = "Order", EntityId = "42" })).Items.Single().Id);
        Assert.Equal(entries[0].Id, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, IsOrphan = false })).Items.Single().Id);
        Assert.Equal(5, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, IsOrphan = true })).TotalCount);
        Assert.Equal(entries[3].Id, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, Checksum = "sum3" })).Items.Single().Id);
        Assert.Equal(8, (await Store.QueryAsync(new FileEntryQuery { StorageName = storage, IncludeDeleted = true })).TotalCount);

        var window = await Store.QueryAsync(new FileEntryQuery { StorageName = storage, UploadedFrom = t0.AddHours(1), UploadedTo = t0.AddHours(3), Descending = false });
        Assert.Equal(new[] { entries[1].Id, entries[2].Id }, window.Items.Select(e => e.Id));
    }

    [Fact]
    public async Task Usage_is_grouped_by_instance_and_category_and_counts_variants()
    {
        var a = "u-a-" + Guid.NewGuid().ToString("N")[..6];
        var b = "u-b-" + Guid.NewGuid().ToString("N")[..6];
        var original = await AddAsync(Entry(a, "Docs", size: 100));
        await AddAsync(Entry(a, "Docs", size: 5, parent: original.Id));
        await AddAsync(Entry(a, "Photos", size: 50));
        await AddAsync(Entry(b, "Docs", size: 1000));
        var gone = await AddAsync(Entry(b, "Docs", size: 9999));
        await Store.MarkDeletedAsync(gone.Id);

        var byInstance = (await Store.GetUsageAsync(new FileUsageQuery { GroupBy = FileUsageGrouping.StorageName }))
            .Where(u => u.StorageName == a || u.StorageName == b).ToList();
        Assert.Equal([new FileUsage(a, null, 3, 155), new FileUsage(b, null, 1, 1000)], byInstance);

        var both = await Store.GetUsageAsync(new FileUsageQuery { GroupBy = FileUsageGrouping.StorageName | FileUsageGrouping.Category, StorageName = a });
        Assert.Equal([new FileUsage(a, "Docs", 2, 105), new FileUsage(a, "Photos", 1, 50)], both);

        var total = await Store.GetUsageAsync(new FileUsageQuery { GroupBy = FileUsageGrouping.None, StorageName = b, IncludeDeleted = true });
        Assert.Equal([new FileUsage(null, null, 2, 10999)], total);

        var legacy = await AddAsync(Entry(null, "Legacy-" + Guid.NewGuid().ToString("N")[..6], size: 3));
        var defaults = await Store.GetUsageAsync(new FileUsageQuery { GroupBy = FileUsageGrouping.StorageName, Category = legacy.Category });
        Assert.Equal([new FileUsage("default", null, 1, 3)], defaults);
    }

    [Fact]
    public async Task Duplicates_are_scoped_to_an_instance_unless_requested_and_paged()
    {
        var category = "Dup-" + Guid.NewGuid().ToString("N")[..8];
        var x = await AddAsync(Entry("dup-x", category, checksum: "same", uploaded: DateTimeOffset.UtcNow.AddHours(-3)));
        var y = await AddAsync(Entry("dup-x", category, checksum: "same", uploaded: DateTimeOffset.UtcNow.AddHours(-2)));
        var other = await AddAsync(Entry("dup-y", category, checksum: "same"));
        await AddAsync(Entry("dup-x", category, checksum: "unique"));
        await AddAsync(Entry("dup-x", category, checksum: "pair", uploaded: DateTimeOffset.UtcNow.AddHours(-1)));
        await AddAsync(Entry("dup-x", category, checksum: "pair"));
        await AddAsync(Entry("dup-x", category, checksum: "pair"));
        await AddAsync(Entry("dup-x", category, checksum: "same", parent: x.Id)); // variants are not duplicates

        var scoped = await Store.FindDuplicatesAsync("dup-x", category);
        Assert.Equal(2, scoped.TotalGroups);
        Assert.Equal(("pair", "dup-x", 3), (scoped.Groups[0].Checksum, scoped.Groups[0].StorageName, scoped.Groups[0].Count));
        Assert.Equal(new[] { x.Id, y.Id }, scoped.Groups[1].EntryIds);

        var page = await Store.FindDuplicatesAsync("dup-x", category, minCount: 2, skip: 1, take: 1);
        Assert.Equal("same", page.Groups.Single().Checksum);

        Assert.DoesNotContain((await Store.FindDuplicatesAsync(new FileDuplicateQuery { Category = category })).Groups, g => g.EntryIds.Contains(other.Id));
        var across = await Store.FindDuplicatesAsync(new FileDuplicateQuery { Category = category, AcrossStorageInstances = true });
        var same = across.Groups.Single(g => g.Checksum == "same");
        Assert.Null(same.StorageName);
        Assert.Equal(3, same.Count);
        Assert.Contains(other.Id, same.EntryIds);
    }

    [Fact]
    public async Task Relocate_keeps_id_and_links()
    {
        var entry = await AddAsync(Entry("src"));
        await Store.LinkAsync(entry.Id, "Employee", "7");

        await Store.RelocateAsync(entry.Id, "dst", "new/location.pdf", "Files/new/location.pdf", encrypted: true, "Fake",
            new Dictionary<string, string> { ["k"] = "v" });

        var moved = await Store.FindAsync(entry.Id);
        Assert.Equal(("dst", "new/location.pdf", "Files/new/location.pdf", true, "Fake", "v"),
            (moved!.StorageName, moved.FileLocation, moved.StoragePath, moved.Encrypted, moved.StorageProvider, moved.Metadata!["k"]));
        Assert.Single(await Store.ListByEntityAsync("Employee", "7"));
        Assert.NotNull(await Store.FindByLocationAsync("new/location.pdf", "dst"));
    }

    [Fact]
    public async Task Supersede_moves_links_lists_versions_and_hides_previous_versions_from_orphans()
    {
        var storage = "v-" + Guid.NewGuid().ToString("N")[..8];
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        var v1 = await AddAsync(Entry(storage, uploaded: old));
        await Store.LinkAsync(v1.Id, "Contract", "1");
        var v2 = await AddAsync(Entry(storage, uploaded: old.AddMinutes(1)));
        await Store.SupersedeAsync(v1.Id, v2.Id);
        var v3 = await AddAsync(Entry(storage, uploaded: old.AddMinutes(2)));
        await Store.SupersedeAsync(v2.Id, v3.Id);

        var stored1 = await Store.FindAsync(v1.Id);
        var stored3 = await Store.FindAsync(v3.Id);
        Assert.Equal(v2.Id, stored1!.SupersededById);
        Assert.Equal((v2.Id, 3), (stored3!.PreviousVersionId, stored3.VersionNumber));
        Assert.Equal(v3.Id, (await Store.ListByEntityAsync("Contract", "1")).Single().Id);

        var versions = await Store.ListVersionsAsync(v1.Id);
        Assert.Equal(new[] { v3.Id, v2.Id, v1.Id }, versions.Select(v => v.Id));

        var orphans = await Store.ListOrphansAsync(DateTimeOffset.UtcNow, 1000, storage);
        Assert.Empty(orphans);
        Assert.DoesNotContain(await Store.ListOrphansAsync(DateTimeOffset.UtcNow, 1000), o => o.Id == v1.Id || o.Id == v2.Id);
        var withVersions = await Store.ListOrphansAsync(DateTimeOffset.UtcNow, 1000, storage, includeSupersededVersions: true);
        Assert.Equal(new[] { v1.Id, v2.Id }, withVersions.Select(o => o.Id));
    }

    [Fact]
    public async Task Orphans_can_be_limited_to_an_instance()
    {
        var before = DateTimeOffset.UtcNow.AddDays(-1);
        var a = await AddAsync(Entry("orph-a", uploaded: before));
        var b = await AddAsync(Entry("orph-b", uploaded: before));
        var orphans = await Store.ListOrphansAsync(DateTimeOffset.UtcNow, 1000, "orph-a");
        Assert.Contains(orphans, o => o.Id == a.Id);
        Assert.DoesNotContain(orphans, o => o.Id == b.Id);
    }
}

public sealed class InMemoryFileEntryStoreContractTests : FileEntryStoreContractTests
{
    protected override IFileEntryStore Store { get; } = new InMemoryFileEntryStore();
}

public sealed class EfFileEntryStoreContractTests : FileEntryStoreContractTests, IAsyncLifetime
{
    private readonly SqliteConnection _db = new("DataSource=:memory:");
    private ServiceProvider _sp = null!;

    protected override IFileEntryStore Store => _sp.GetRequiredService<IFileEntryStore>();

    public async Task InitializeAsync()
    {
        await _db.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<KitStackStorageDbContext>(o => o.UseSqlite(_db));
        services.AddSingleton<IFileEntryStore, EfFileEntryStore<KitStackStorageDbContext>>();
        _sp = services.BuildServiceProvider();
        await using var db = await _sp.GetRequiredService<IDbContextFactory<KitStackStorageDbContext>>().CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public void Model_maps_the_new_columns_and_indexes()
    {
        using var db = _sp.GetRequiredService<IDbContextFactory<KitStackStorageDbContext>>().CreateDbContext();
        var type = db.Model.FindEntityType(typeof(KitStack.Storage.EntityFrameworkCore.Entities.FileEntryRecord))!;
        Assert.Equal(100, type.FindProperty("StorageName")!.GetMaxLength());
        Assert.Equal(1, type.FindProperty("VersionNumber")!.GetDefaultValue());
        Assert.NotNull(type.FindProperty("PreviousVersionId"));
        Assert.NotNull(type.FindProperty("SupersededById"));
        Assert.Contains(type.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(["StorageName", "IsDeleted"]));
    }
}

public sealed class MongoFileEntryStoreContractTests(MongoFixture mongo) : FileEntryStoreContractTests, IClassFixture<MongoFixture>
{
    protected override IFileEntryStore Store { get; } = new MongoFileEntryStore(mongo.NewDatabase());

    [Fact]
    public async Task Documents_without_the_new_fields_read_as_default_version_one()
    {
        var database = mongo.NewDatabase();
        var store = new MongoFileEntryStore(database);
        var id = Guid.NewGuid();
        await database.GetCollection<MongoDB.Bson.BsonDocument>(MongoFileEntryStore.DefaultCollectionName).InsertOneAsync(new MongoDB.Bson.BsonDocument
        {
            { "_id", new MongoDB.Bson.BsonBinaryData(id, MongoDB.Bson.GuidRepresentation.Standard) },
            { "FileName", "old.pdf" },
            { "FileLocation", "old/old.pdf" },
            { "ContentType", "application/pdf" },
            { "UploadedTime", DateTime.UtcNow },
            { "IsDeleted", false },
            { "Links", new MongoDB.Bson.BsonArray() },
        });

        var entry = await store.FindAsync(id);
        Assert.Equal((null, 1, (Guid?)null), (entry!.StorageName, entry.VersionNumber, entry.SupersededById));
        Assert.NotNull(await store.FindByLocationAsync("old/old.pdf", "default"));

        var indexes = await (await store.Collection.Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, i => i["name"].AsString == "StorageName_1_IsDeleted_1");
    }
}
