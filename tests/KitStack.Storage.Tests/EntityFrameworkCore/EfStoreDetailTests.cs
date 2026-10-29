using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Fakes.Extensions;
using KitStack.Storage.EntityFrameworkCore;
using KitStack.Storage.EntityFrameworkCore.Configuration;
using KitStack.Storage.EntityFrameworkCore.Entities;
using KitStack.Storage.EntityFrameworkCore.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.Tests.EntityFrameworkCore;

public sealed class Council
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<StoredFileRef> Minutes { get; set; } = [];
}

public sealed class CouncilDbContext(DbContextOptions<CouncilDbContext> options) : DbContext(options)
{
    public DbSet<Council> Councils => Set<Council>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyKitStackStorage(this, schema: "files");
        modelBuilder.Entity<Council>().OwnsStoredFiles(c => c.Minutes, "CouncilMinutes");
    }
}

public sealed class EfStoreDetailTests : IAsyncLifetime
{
    private readonly SqliteConnection _db = new("DataSource=:memory:");
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        await _db.OpenAsync();
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddEntityFrameworkCoreStore<CouncilDbContext>();
        services.AddDbContext<CouncilDbContext>((sp, o) => o.UseSqlite(_db).AddKitStackFileReferences(sp));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CouncilDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<IFileEntry> UploadAsync(IServiceProvider scope, string name)
        => await scope.GetRequiredService<IFileStorageManager>().CreateAsync<Council>(new MemoryStream(TestData.Text(name)), name, null, "Council");

    [Fact]
    public async Task Owned_collections_link_and_release_each_file_with_synchronous_saves()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CouncilDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IFileEntryStore>();
        var a = await UploadAsync(scope.ServiceProvider, "a.txt");
        var b = await UploadAsync(scope.ServiceProvider, "b.txt");

        var council = new Council { Minutes = [StoredFileRef.From(a), StoredFileRef.From(b)] };
        db.Add(council);
        db.SaveChanges();
        Assert.Equal(2, (await store.ListByEntityAsync(nameof(Council), council.Id.ToString())).Count);

        council.Minutes.RemoveAt(0);
        db.SaveChanges();
        Assert.Equal([b.Id], (await store.ListByEntityAsync(nameof(Council), council.Id.ToString())).Select(e => e.Id));
        Assert.Equal([a.Id], (await store.ListOrphansAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10)).Select(e => e.Id));
    }

    [Fact]
    public async Task Files_stored_without_tracking_are_recorded_when_first_referenced()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CouncilDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IFileEntryStore>();
        var untracked = new FileEntry { FileLocation = "legacy/minutes.pdf", FileName = "minutes.pdf", ContentType = "application/pdf", Size = 3, Checksum = "abc" };

        var council = new Council { Minutes = [StoredFileRef.From(untracked)] };
        db.Add(council);
        await db.SaveChangesAsync();

        var recorded = await store.FindAsync(untracked.Id);
        Assert.Equal(("legacy/minutes.pdf", "application/pdf", "abc"), (recorded!.FileLocation, recorded.ContentType, recorded.Checksum));
        Assert.Single(recorded.RelatedEntities!);
    }

    [Fact]
    public async Task A_failed_save_records_no_links()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CouncilDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IFileEntryStore>();
        var file = await UploadAsync(scope.ServiceProvider, "x.txt");
        var existing = new Council();
        db.Add(existing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Add(new Council { Id = existing.Id, Minutes = [StoredFileRef.From(file)] }); // duplicate key
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Empty((await store.FindAsync(file.Id))!.RelatedEntities!);
    }

    [Fact]
    public async Task Store_updates_ignores_unknowns_and_skips_duplicates()
    {
        await using var scope = _sp.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IFileEntryStore>();
        var file = await UploadAsync(scope.ServiceProvider, "u.txt");

        file.Checksum = "new";
        file.Size = 99;
        file.Metadata = new Dictionary<string, string> { ["k"] = "v" };
        await store.UpdateAsync(file);
        await store.AddAsync(file);
        var updated = await store.FindAsync(file.Id);
        Assert.Equal(("new", 99L, "v"), (updated!.Checksum, updated.Size, updated.Metadata!["k"]));

        var unknown = Guid.NewGuid();
        await store.UpdateAsync(new FileEntry { Id = unknown });
        await store.LinkAsync(unknown, "X", "1");
        await store.UnlinkAsync(unknown, "X", "1");
        await store.UnlinkAsync(file.Id, "X", "never-linked");
        await store.MarkDeletedAsync(unknown);
        Assert.Null(await store.FindAsync(unknown));
        Assert.Null(await store.FindByLocationAsync("nowhere"));

        await store.MarkDeletedAsync(file.Id);
        await store.MarkDeletedAsync(file.Id);
        Assert.Null(await store.FindByLocationAsync(file.FileLocation));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListByEntityAsync(" ", "1"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListOrphansAsync(DateTimeOffset.UtcNow, 0));
    }

    [Fact]
    public void Model_uses_the_configured_tables_schema_and_limits()
    {
        using var scope = _sp.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CouncilDbContext>().Model;
        var entries = model.FindEntityType(typeof(FileEntryRecord))!;
        Assert.Equal(("FileEntries", "files"), (entries.GetTableName(), entries.GetSchema()));
        Assert.Equal(1024, entries.FindProperty(nameof(FileEntryRecord.FileLocation))!.GetMaxLength());
        Assert.Contains(entries.GetIndexes(), i => i.Properties.Single().Name == nameof(FileEntryRecord.ParentId));
        var links = entries.FindNavigation(nameof(FileEntryRecord.RelatedEntities))!.TargetEntityType;
        Assert.Equal("FileEntryLinks", links.GetTableName());
        Assert.Equal("CouncilMinutes", model.FindEntityType(typeof(Council))!.FindNavigation(nameof(Council.Minutes))!.TargetEntityType.GetTableName());
    }

    [Fact]
    public void Record_mapping_copies_relations_and_parent()
    {
        var original = Guid.NewGuid();
        var entry = new FileEntry { Metadata = new Dictionary<string, string> { ["OriginalId"] = original.ToString("N") } };
        entry.RelatedEntities!.Add(new FileRelatedEntity { EntityId = "1", EntityName = "A", Notes = "n" });

        var record = FileEntryRecord.From(entry);

        Assert.Equal(original, record.ParentId);
        Assert.Equal(("1", "A", "n"), (record.RelatedEntities!.Single().EntityId, record.RelatedEntities!.Single().EntityName, record.RelatedEntities!.Single().Notes));
        Assert.Null(FileEntryRecord.From(new FileEntry { Metadata = null, RelatedEntities = null }).ParentId);
        Assert.Throws<ArgumentNullException>(() => FileEntryRecord.From(null!));
    }

    [Fact]
    public void Registration_arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => ((KitStackStorageBuilder)null!).AddEntityFrameworkCoreStore<CouncilDbContext>());
        Assert.Throws<ArgumentNullException>(() => ((KitStackStorageBuilder)null!).AddKitStackStorageDbContext(_ => { }));
        Assert.Throws<ArgumentNullException>(() => new DbContextOptionsBuilder().AddKitStackFileReferences(null!));
        Assert.Throws<ArgumentNullException>(() => ((ModelBuilder)null!).ApplyKitStackStorage());
        using var scope = _sp.CreateScope();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKitStackStorage().AddEntityFrameworkCoreStore<KitStackStorageDbContext>());
    }
}
