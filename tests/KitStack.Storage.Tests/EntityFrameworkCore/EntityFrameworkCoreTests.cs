using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Services;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Extensions;
using KitStack.Storage.EntityFrameworkCore;
using KitStack.Storage.EntityFrameworkCore.Configuration;
using KitStack.Storage.EntityFrameworkCore.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.Tests.EntityFrameworkCore;

public sealed class Contract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public StoredFileRef? Document { get; set; }
}

public sealed class Letter
{
    public int Id { get; set; }
    public StoredFileRef? Scan { get; set; }
}

/// <summary>Application context that also holds the file metadata tables.</summary>
public sealed class AppWithMetadataDbContext(DbContextOptions<AppWithMetadataDbContext> options) : DbContext(options)
{
    public DbSet<Contract> Contracts => Set<Contract>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyKitStackStorage(this);
        modelBuilder.Entity<Contract>().OwnsStoredFile(c => c.Document);
    }
}

/// <summary>Application context without metadata tables (metadata lives in KitStackStorageDbContext).</summary>
public sealed class AppOnlyDbContext(DbContextOptions<AppOnlyDbContext> options) : DbContext(options)
{
    public DbSet<Letter> Letters => Set<Letter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Letter>().OwnsStoredFile(l => l.Scan);
}

public sealed class EntityFrameworkCoreTests : IDisposable
{
    private readonly SqliteConnection _appDb = Open();
    private readonly SqliteConnection _metaDb = Open();

    public void Dispose()
    {
        _appDb.Dispose();
        _metaDb.Dispose();
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task Same_context_links_follow_StoredFileRef_changes_and_orphans_are_swept()
    {
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddEntityFrameworkCoreStore<AppWithMetadataDbContext>();
        services.AddDbContext<AppWithMetadataDbContext>((sp, o) => o.UseSqlite(_appDb).AddKitStackFileReferences(sp));
        await using var sp = services.BuildServiceProvider();

        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppWithMetadataDbContext>().Database.EnsureCreatedAsync();

        await using var main = sp.CreateAsyncScope();
        var storage = main.ServiceProvider.GetRequiredService<IFileStorageManager>();
        var store = main.ServiceProvider.GetRequiredService<IFileEntryStore>();
        var db = main.ServiceProvider.GetRequiredService<AppWithMetadataDbContext>();

        // Upload is recorded by the tracking decorator.
        var first = await storage.CreateAsync<Contract>(new MemoryStream(TestData.Pdf()), "v1.pdf", null, "Legal");
        Assert.NotNull(await store.FindAsync(first.Id));

        // Setting the reference links the file to the entity.
        var contract = new Contract { Title = "Lease", Document = StoredFileRef.From(first) };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        Assert.Equal([first.Id], (await store.ListByEntityAsync(nameof(Contract), contract.Id.ToString())).Select(e => e.Id));

        // Replacing it releases the old file.
        var second = await storage.CreateAsync<Contract>(new MemoryStream(TestData.Text("v2")), "v2.txt", null, "Legal");
        contract.Document = StoredFileRef.From(second);
        await db.SaveChangesAsync();
        Assert.Equal([second.Id], (await store.ListByEntityAsync(nameof(Contract), contract.Id.ToString())).Select(e => e.Id));
        Assert.Equal([first.Id], (await store.ListOrphansAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10)).Select(e => e.Id));

        // The sweeper deletes the bytes and the record of the orphan only.
        var swept = await main.ServiceProvider.GetRequiredService<FileOrphanSweeper>().SweepAsync(TimeSpan.Zero);
        Assert.Equal(1, swept);
        var fake = sp.GetRequiredService<IFakeFileStore>();
        Assert.False(fake.TryGetFile(first.FileLocation, out _));
        Assert.True(fake.TryGetFile(second.FileLocation, out _));
        Assert.True((await store.FindAsync(first.Id))!.IsDeleted);

        // Deleting the owner releases its file.
        db.Contracts.Remove(contract);
        await db.SaveChangesAsync();
        Assert.Equal([second.Id], (await store.ListOrphansAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10)).Select(e => e.Id));
    }

    [Fact]
    public async Task Separate_metadata_database_links_after_save_for_generated_keys()
    {
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddKitStackStorageDbContext(o => o.UseSqlite(_metaDb), trackUploads: false);
        services.AddDbContext<AppOnlyDbContext>((sp, o) => o.UseSqlite(_appDb).AddKitStackFileReferences(sp));
        await using var sp = services.BuildServiceProvider();

        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppOnlyDbContext>().Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<KitStackStorageDbContext>().Database.EnsureCreatedAsync();
        }

        await using var main = sp.CreateAsyncScope();
        var storage = main.ServiceProvider.GetRequiredService<IFileStorageManager>();
        var store = main.ServiceProvider.GetRequiredService<IFileEntryStore>();
        var db = main.ServiceProvider.GetRequiredService<AppOnlyDbContext>();

        var scan = await storage.CreateAsync<Letter>(new MemoryStream(TestData.Png()), "scan.png", null, "Diwan");
        Assert.Null(await store.FindAsync(scan.Id)); // tracking disabled

        var letter = new Letter { Scan = StoredFileRef.From(scan) };
        db.Letters.Add(letter);
        await db.SaveChangesAsync();

        // The record is created from the reference, linked to the generated key.
        Assert.True(letter.Id > 0);
        var linked = await store.ListByEntityAsync(nameof(Letter), letter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var record = Assert.Single(linked);
        Assert.Equal(scan.Checksum, record.Checksum);
        Assert.Equal("image/png", record.ContentType);

        letter.Scan = null;
        await db.SaveChangesAsync();
        Assert.Empty(await store.ListByEntityAsync(nameof(Letter), letter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Deleting_through_the_tracked_manager_marks_the_original_and_its_variants()
    {
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddKitStackStorageDbContext(o => o.UseSqlite(_metaDb));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<KitStackStorageDbContext>().Database.EnsureCreatedAsync();

        await using var main = sp.CreateAsyncScope();
        var storage = main.ServiceProvider.GetRequiredService<IFileStorageManager>();
        var store = main.ServiceProvider.GetRequiredService<IFileEntryStore>();

        var (primary, variants) = await storage.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");
        Assert.Equal(2, variants.Count);
        Assert.NotNull(await store.FindByLocationAsync(primary.FileLocation));

        // Variants are not orphans on their own.
        Assert.Equal([primary.Id], (await store.ListOrphansAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10)).Select(e => e.Id));

        await storage.DeleteAsync(primary);

        foreach (var id in variants.Select(v => v.Id).Append(primary.Id))
            Assert.True((await store.FindAsync(id))!.IsDeleted);
        Assert.Null(await store.FindByLocationAsync(primary.FileLocation));
        Assert.Empty(sp.GetRequiredService<IFakeFileStore>().ListFiles());
    }

    [Fact]
    public async Task Links_from_CreateAsync_with_entity_are_persisted()
    {
        var services = new ServiceCollection();
        services.AddKitStackStorage().AddFake().AddKitStackStorageDbContext(o => o.UseSqlite(_metaDb));
        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<KitStackStorageDbContext>().Database.EnsureCreatedAsync();

        await using var main = sp.CreateAsyncScope();
        var store = main.ServiceProvider.GetRequiredService<IFileEntryStore>();

        var entry = await main.ServiceProvider.GetRequiredService<IFileStorageManager>()
            .CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Billing");
        await store.LinkAsync(entry.Id, "Invoice", "42", "attachment");
        await store.LinkAsync(entry.Id, "Invoice", "42");
        var record = Assert.Single(await store.ListByEntityAsync("Invoice", "42"));
        Assert.Single(record.RelatedEntities!);

        await store.UnlinkAsync(entry.Id, "Invoice", "42");
        Assert.Empty(await store.ListByEntityAsync("Invoice", "42"));
        Assert.Single(await store.ListByEntityAsync("Invoice", "42", includeUnused: true));
    }
}
