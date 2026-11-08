using KitStack.Storage.Abstractions.DependencyInjection;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Services;
using KitStack.Storage.Fakes.Contracts;
using KitStack.Storage.Fakes.Extensions;
using KitStack.Storage.Fakes.Stores;
using KitStack.Storage.Local.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.Tests.Instances;

public sealed class TransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kitstack-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly string _key = TestData.NewKey();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ServiceProvider Build(FlakyStore? store = null)
    {
        var services = new ServiceCollection();
        if (store is not null)
            services.AddSingleton<IFileEntryStore>(store);
        services.AddKitStackStorage()
            .AddLocal("local", o => o.Path = _root)
            .AddFake("fake")
            .AddFake("secure")
            .ConfigureInstance("secure", o => o.MasterEncryptionKey = _key)
            .AddInMemoryStore();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Local_to_fake_with_variants_keeps_id_links_folder_and_deletes_the_source_after_verification()
    {
        await using var sp = Build();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var store = sp.GetRequiredService<IFileEntryStore>();

        var (entry, variants) = await provider.Get("local").CreateWithVariantsAsync(new StorageWriteOptions
        {
            FileName = "truck.png",
            Folder = "fms/vehicles/images",
            Metadata = new Dictionary<string, string> { ["Plate"] = "AB-123" },
            Link = new StorageEntityLink { EntityName = "Vehicle", EntityId = "9" },
        }, new MemoryStream(TestData.Png()));
        Assert.Equal(2, variants.Count);
        var sourceFiles = new[] { entry }.Concat(variants).Select(e => Path.Combine(_root, e.FileLocation)).ToList();
        Assert.All(sourceFiles, f => Assert.True(File.Exists(f)));

        var result = await sp.GetRequiredService<IFileTransferService>().TransferAsync(entry, "fake", new FileTransferOptions { DeleteSource = true });

        Assert.True(result.Succeeded, string.Join("; ", result.Steps));
        Assert.Equal(("local", "fake"), (result.SourceStorageName, result.TargetStorageName));
        Assert.Contains(result.Steps, s => s is { Name: "Relocate", Status: FileTransferStepStatus.Completed });
        Assert.Contains(result.Steps, s => s is { Name: "DeleteSource", Status: FileTransferStepStatus.Completed });
        Assert.All(sourceFiles, f => Assert.False(File.Exists(f)));

        var moved = await store.FindAsync(entry.Id);
        Assert.Equal(("fake", "Fake", entry.FileLocation, "AB-123", "fms/vehicles/images"),
            (moved!.StorageName, moved.StorageProvider, moved.FileLocation, moved.Metadata!["Plate"], moved.Metadata[FileEntryMetadataKeys.Folder]));
        Assert.StartsWith("fms/vehicles/images/", moved.FileLocation, StringComparison.Ordinal);
        // Variants carry their original's link, so the original and both variants are still found by the entity.
        Assert.Equal(new[] { entry.Id }.Concat(variants.Select(v => v.Id)).Order(), (await store.ListByEntityAsync("Vehicle", "9")).Select(e => e.Id).Order());
        Assert.Equal(TestData.Png(), await TestData.ReadAllAsync(await provider.Get(moved).OpenReadAsync(moved)));

        foreach (var variant in variants)
        {
            var record = await store.FindAsync(variant.Id);
            Assert.Equal("fake", record!.StorageName);
            Assert.True(await provider.Get(record).ExistsAsync(record));
        }

        Assert.Equal(3, sp.GetRequiredKeyedService<IFakeFileStore>("fake").ListFiles().Count);
        // Deleting through the target removes the variants recorded in the re-pointed metadata.
        await provider.Get(moved).DeleteAsync(moved);
        Assert.Empty(sp.GetRequiredKeyedService<IFakeFileStore>("fake").ListFiles());
    }

    [Fact]
    public async Task Encrypted_source_is_re_encrypted_for_the_target_or_stored_in_plaintext()
    {
        await using var sp = Build();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var entry = await provider.Get("secure").CreateAsync(new StorageWriteOptions { FileName = "a.pdf", Folder = "archiving" }, new MemoryStream(TestData.Pdf()));
        Assert.True(entry.Encrypted);

        var result = await sp.GetRequiredService<IFileTransferService>().TransferAsync(entry, "fake");

        Assert.True(result.Succeeded);
        Assert.Contains(result.Steps, s => s is { Name: "DeleteSource", Status: FileTransferStepStatus.Skipped });
        Assert.False(entry.Encrypted);
        Assert.Equal("fake", entry.StorageName);
        Assert.Equal(TestData.Pdf(), sp.GetRequiredKeyedService<IFakeFileStore>("fake").ListFiles().Single().Content);
        Assert.Single(sp.GetRequiredKeyedService<IFakeFileStore>("secure").ListFiles());

        var back = await sp.GetRequiredService<IFileTransferService>().TransferAsync(entry, "secure");
        Assert.True(back.Succeeded);
        Assert.True(entry.Encrypted);
        Assert.Contains(back.Steps, s => s is { Name: "Copy original", Status: FileTransferStepStatus.Skipped }); // same bytes already there
        Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await provider.Get(entry).OpenReadAsync(entry)));
    }

    [Fact]
    public async Task Checksum_mismatch_aborts_without_deleting_the_source()
    {
        await using var sp = Build();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var entry = await provider.Get("local").CreateAsync(new StorageWriteOptions { FileName = "a.txt", Folder = "docs" }, new MemoryStream(TestData.Text()));
        var original = entry.FileLocation;
        entry.Checksum = new string('0', 64); // the recorded checksum no longer matches the bytes

        var result = await sp.GetRequiredService<IFileTransferService>().TransferAsync(entry, "fake", new FileTransferOptions { DeleteSource = true });

        Assert.False(result.Succeeded);
        Assert.Equal(FileTransferStepStatus.Failed, result.Steps.Single(s => s.Name == "Verify original").Status);
        Assert.DoesNotContain(result.Steps, s => s.Name is "Relocate" or "DeleteSource");
        Assert.True(File.Exists(Path.Combine(_root, original)));
        Assert.Empty(sp.GetRequiredKeyedService<IFakeFileStore>("fake").ListFiles());
        Assert.Equal("local", (await sp.GetRequiredService<IFileEntryStore>().FindAsync(entry.Id))!.StorageName);
    }

    [Fact]
    public async Task Rerun_after_a_partial_failure_resumes_without_copying_again()
    {
        var store = new FlakyStore { FailRelocations = 1 };
        await using var sp = Build(store);
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var (entry, _) = await provider.Get("local").CreateWithVariantsAsync(new StorageWriteOptions { FileName = "p.png", Folder = "img" }, new MemoryStream(TestData.Png()));
        await store.LinkAsync(entry.Id, "Vehicle", "1");
        var transfers = sp.GetRequiredService<IFileTransferService>();

        var failed = await transfers.TransferAsync(entry, "fake", new FileTransferOptions { DeleteSource = true });
        Assert.False(failed.Succeeded);
        Assert.Equal(FileTransferStepStatus.Failed, failed.Steps.Single(s => s.Name == "Relocate").Status);
        Assert.Equal("local", entry.StorageName);
        Assert.Equal(3, sp.GetRequiredKeyedService<IFakeFileStore>("fake").ListFiles().Count);
        Assert.True(await provider.Get(entry).ExistsAsync(entry));

        var resumed = await transfers.TransferAsync(entry, "fake", new FileTransferOptions { DeleteSource = true });
        Assert.True(resumed.Succeeded, string.Join("; ", resumed.Steps));
        Assert.All(resumed.Steps.Where(s => s.Name.StartsWith("Copy", StringComparison.Ordinal)), s => Assert.Equal(FileTransferStepStatus.Skipped, s.Status));
        Assert.Equal("fake", (await store.FindAsync(entry.Id))!.StorageName);
        Assert.Equal(entry.Id, (await store.ListByEntityAsync("Vehicle", "1")).Single().Id);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));

        // Running it again is a no-op.
        var again = await transfers.TransferAsync(entry, "fake");
        Assert.Equal(FileTransferStepStatus.Skipped, again.Steps.Single().Status);
    }

    [Fact]
    public async Task References_are_refreshed_by_id_after_a_transfer()
    {
        await using var sp = Build();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var store = sp.GetRequiredService<IFileEntryStore>();
        var entry = await provider.Get("local").CreateAsync(new StorageWriteOptions { FileName = "a.txt", Folder = "docs" }, new MemoryStream(TestData.Text()));
        var reference = StoredFileRef.From(entry);
        Assert.Equal("local", reference.StorageName);

        await sp.GetRequiredService<IFileTransferService>().TransferAsync(entry, "fake", new FileTransferOptions { DeleteSource = true });

        var refreshed = StoredFileRef.From((await store.FindManyAsync([reference.FileEntryId])).Single());
        Assert.Equal("fake", refreshed.StorageName);
        Assert.Equal(TestData.Text(), await TestData.ReadAllAsync(await provider.Get(refreshed).OpenReadAsync(refreshed.ToFileEntry())));
    }

    /// <summary>In-memory store whose relocation fails a given number of times.</summary>
    internal sealed class FlakyStore : IFileEntryStore
    {
        private readonly InMemoryFileEntryStore _inner = new();

        public int FailRelocations { get; set; }

        public Task RelocateAsync(IReadOnlyCollection<FileRelocation> relocations, CancellationToken cancellationToken = default)
        {
            if (FailRelocations-- > 0)
                throw new IOException("database unavailable");
            return _inner.RelocateAsync(relocations, cancellationToken);
        }

        public Task AddAsync(IFileEntry entry, CancellationToken cancellationToken = default) => _inner.AddAsync(entry, cancellationToken);
        public Task UpdateAsync(IFileEntry entry, CancellationToken cancellationToken = default) => _inner.UpdateAsync(entry, cancellationToken);
        public Task<IFileEntry?> FindAsync(Guid id, CancellationToken cancellationToken = default) => _inner.FindAsync(id, cancellationToken);
        public Task<IFileEntry?> FindByLocationAsync(string fileLocation, CancellationToken cancellationToken = default) => _inner.FindByLocationAsync(fileLocation, cancellationToken);
        public Task<IReadOnlyList<IFileEntry>> ListByEntityAsync(string entityName, string entityId, bool includeUnused = false, CancellationToken cancellationToken = default) => _inner.ListByEntityAsync(entityName, entityId, includeUnused, cancellationToken);
        public Task LinkAsync(Guid fileEntryId, string entityName, string entityId, string? notes = null, CancellationToken cancellationToken = default) => _inner.LinkAsync(fileEntryId, entityName, entityId, notes, cancellationToken);
        public Task UnlinkAsync(Guid fileEntryId, string entityName, string entityId, CancellationToken cancellationToken = default) => _inner.UnlinkAsync(fileEntryId, entityName, entityId, cancellationToken);
        public Task MarkDeletedAsync(Guid id, CancellationToken cancellationToken = default) => _inner.MarkDeletedAsync(id, cancellationToken);
        public Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(DateTimeOffset uploadedBefore, int maxCount, CancellationToken cancellationToken = default) => _inner.ListOrphansAsync(uploadedBefore, maxCount, cancellationToken);
        public Task<FileEntryPage> QueryAsync(FileEntryQuery query, CancellationToken cancellationToken = default) => _inner.QueryAsync(query, cancellationToken);
    }
}

public sealed class VersionTests
{
    private static ServiceProvider Build()
        => new ServiceCollection().AddLogging().AddKitStackStorage().AddFake().AddFake("docs").AddInMemoryStore().Services.BuildServiceProvider();

    private static StorageWriteOptions Doc(string name = "contract.txt") => new() { FileName = name, Folder = "legal/contracts", Category = "Legal" };

    [Fact]
    public async Task Keep_previous_list_restore_and_purge()
    {
        await using var sp = Build();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var store = sp.GetRequiredService<IFileEntryStore>();
        var versions = sp.GetRequiredService<IFileVersionService>();

        var v1 = await provider.Get("docs").CreateAsync(Doc(), new MemoryStream(TestData.Text("v1")));
        await store.LinkAsync(v1.Id, "Contract", "5");
        v1 = (await store.FindAsync(v1.Id))!;

        var v2 = await versions.ReplaceAsync(v1, Doc(), new MemoryStream(TestData.Text("v2")), new ReplaceOptions { KeepPreviousVersion = true });
        Assert.Equal((v1.Id, 2, "docs"), (v2.PreviousVersionId, v2.VersionNumber, v2.StorageName));
        Assert.Equal(v2.Id, v1.SupersededById);
        var storedV1 = await store.FindAsync(v1.Id);
        Assert.Equal(v2.Id, storedV1!.SupersededById);
        Assert.False(storedV1.IsDeleted);
        Assert.True(await provider.Get(storedV1).ExistsAsync(storedV1));
        Assert.Equal(v2.Id, (await store.ListByEntityAsync("Contract", "5")).Single().Id);

        var v3 = await versions.ReplaceAsync(v2, Doc(), new MemoryStream(TestData.Text("v3")), new ReplaceOptions { KeepPreviousVersion = true });
        Assert.Equal(new[] { v3.Id, v2.Id, v1.Id }, (await versions.ListVersionsAsync(v1.Id)).Select(v => v.Id));

        // Restore creates v4 from v1's bytes; history is not mutated.
        var v4 = await versions.RestoreVersionAsync(v1.Id);
        Assert.Equal((v3.Id, 4), (v4.PreviousVersionId, v4.VersionNumber));
        Assert.Equal(TestData.Text("v1"), await TestData.ReadAllAsync(await provider.Get(v4).OpenReadAsync(v4)));
        Assert.StartsWith("legal/contracts/", v4.FileLocation, StringComparison.Ordinal);
        Assert.Equal(TestData.Text("v1"), await TestData.ReadAllAsync(await provider.Get(storedV1).OpenReadAsync(storedV1)));
        Assert.Equal(v4.Id, (await store.ListByEntityAsync("Contract", "5")).Single().Id);
        Assert.Equal(v4.Id, (await versions.RestoreVersionAsync(v4.Id)).Id); // restoring the current version is a no-op

        var purged = await versions.PurgeVersionsAsync(v4.Id, keepLatest: 1);
        Assert.Equal(2, purged); // v2 and v1; v3 kept as the latest previous version
        Assert.Equal(new[] { v4.Id, v3.Id }, (await versions.ListVersionsAsync(v4.Id)).Select(v => v.Id));
        Assert.True((await store.FindAsync(v1.Id))!.IsDeleted);
        Assert.False(await provider.Get(storedV1).ExistsAsync(storedV1));
        Assert.Equal(2, sp.GetRequiredKeyedService<IFakeFileStore>("docs").ListFiles().Count);
    }

    [Fact]
    public async Task Replace_without_keeping_deletes_the_old_file_and_the_sweeper_ignores_retained_versions()
    {
        await using var sp = Build();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();
        var store = sp.GetRequiredService<IFileEntryStore>();
        var versions = sp.GetRequiredService<IFileVersionService>();

        var a1 = await provider.Get("docs").CreateAsync(Doc("a.txt"), new MemoryStream(TestData.Text("a1")));
        var a2 = await versions.ReplaceAsync(a1, Doc("a.txt"), new MemoryStream(TestData.Text("a2")));
        Assert.True((await store.FindAsync(a1.Id))!.IsDeleted);
        Assert.Null(a2.PreviousVersionId);

        var b1 = await provider.Get("docs").CreateAsync(Doc("b.txt"), new MemoryStream(TestData.Text("b1")));
        await store.LinkAsync(b1.Id, "Contract", "8");
        var b2 = await versions.ReplaceAsync((await store.FindAsync(b1.Id))!, Doc("b.txt"), new MemoryStream(TestData.Text("b2")), new ReplaceOptions { KeepPreviousVersion = true });

        // b1 is unlinked and old enough, but it is a retained version; a2 is a genuine orphan.
        using var scope = sp.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<FileOrphanSweeper>().SweepAsync(TimeSpan.Zero, 100);
        Assert.Equal(1, deleted);
        Assert.False((await store.FindAsync(b1.Id))!.IsDeleted);
        Assert.False((await store.FindAsync(b2.Id))!.IsDeleted);
        Assert.True((await store.FindAsync(a2.Id))!.IsDeleted);
    }
}
