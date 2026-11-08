# KitStack.Storage.EntityFrameworkCore

EF Core metadata store for KitStack.Storage. Storage providers keep the bytes; this package keeps track of
what was stored, which entities use each file, and which files have become orphans.

It contains:
- `EfFileEntryStore<TContext>`, an `IFileEntryStore` over tables `FileEntries` and `FileEntryLinks`.
- `OwnsStoredFile(...)`, which maps a `StoredFileRef` property as an owned type (one set of columns instead of
  repeating FileEntryId / location / content type / size / checksum on every entity).
- `FileReferenceInterceptor`, which links a file to its owner when a `StoredFileRef` is set and releases the
  link when it is replaced, cleared or its owner is deleted.
- Works with `FileOrphanSweeper` and the opt-in `AddOrphanSweeper()` hosted service, which delete files that stayed unlinked past a grace period.

## Metadata in its own database

```csharp
builder.Services.AddKitStackStorage(builder.Configuration)
    .AddLocal()                                                    // provider first
    .AddKitStackStorageDbContext(o => o.UseSqlServer(cs));         // records every upload

builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(appCs).AddKitStackFileReferences(sp));          // link/unlink on SaveChanges
```

Create the tables with migrations on `KitStackStorageDbContext`, or `EnsureCreated()` in development.

## Metadata in the application's context

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyKitStackStorage(this);                       // FileEntries + FileEntryLinks
    modelBuilder.Entity<LegalDocument>().OwnsStoredFile(d => d.File);
}

builder.Services.AddKitStackStorage(builder.Configuration).AddLocal().AddEntityFrameworkCoreStore<AppDbContext>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(cs).AddKitStackFileReferences(sp));
```

Links are then written in the same transaction as the entity.

## Using it

```csharp
public class LegalDocument
{
    public Guid Id { get; set; }
    public StoredFileRef? File { get; set; }
}

var entry = await storage.CreateAsync<LegalDocument>(upload, "LegalAffairs");
document.File = StoredFileRef.From(entry);
await db.SaveChangesAsync();                                       // linked to LegalDocument/{Id}

await using var stream = await storage.OpenReadAsync(document.File);  // read back from any provider
```

Replacing `document.File` and saving releases the old file; deleting `document` releases its file.

## Orphan sweeper (opt-in)

```csharp
builder.Services.AddKitStackStorage(builder.Configuration)
    .AddLocal()
    .AddKitStackStorageDbContext(o => o.UseSqlServer(cs))
    .AddOrphanSweeper(o => o.MinAge = TimeSpan.FromDays(2));       // or Storage:OrphanSweeper
```

The sweeper deletes every non-deleted original that was uploaded more than `MinAge` ago and has no active
link. **Only enable it once every place that keeps a file records a link** (through `StoredFileRef` and the
interceptor, `CreateAsync(entity, ...)`, or `IFileEntryStore.LinkAsync`). Files referenced in any other way,
for example by a plain string column, look like orphans and will be deleted.

## Instances, reporting and versions (0.3)

The store records `StorageName` (the instance holding the bytes; `null` for rows written before 0.3 means `default`),
`PreviousVersionId`, `VersionNumber` and `SupersededById`, and implements `QueryAsync`, `GetUsageAsync` (SQL
`GROUP BY`), `FindDuplicatesAsync`, `FindManyAsync`, `RelocateAsync` (one `SaveChanges`, used by transfers),
`SupersedeAsync` / `ListVersionsAsync` and instance-scoped `ListOrphansAsync` / `FindByLocationAsync`.
`OwnsStoredFile` / `OwnsStoredFiles` also map `StoredFileRef.StorageName`.

### Migrating from 0.2

Add a migration (`dotnet ef migrations add KitStackStorage03`). It should contain the equivalent of:

```csharp
migrationBuilder.AddColumn<string>(name: "StorageName", table: "FileEntries", maxLength: 100, nullable: true);
migrationBuilder.AddColumn<Guid>(name: "PreviousVersionId", table: "FileEntries", nullable: true);
migrationBuilder.AddColumn<int>(name: "VersionNumber", table: "FileEntries", nullable: false, defaultValue: 1);
migrationBuilder.AddColumn<Guid>(name: "SupersededById", table: "FileEntries", nullable: true);
migrationBuilder.CreateIndex(name: "IX_FileEntries_StorageName_IsDeleted", table: "FileEntries", columns: ["StorageName", "IsDeleted"]);
migrationBuilder.CreateIndex(name: "IX_FileEntries_StorageName_Checksum", table: "FileEntries", columns: ["StorageName", "Checksum"]);
migrationBuilder.CreateIndex(name: "IX_FileEntries_SupersededById", table: "FileEntries", column: "SupersededById");

// For every entity that maps a StoredFileRef with OwnsStoredFile(e => e.File) (column prefix = navigation name):
migrationBuilder.AddColumn<string>(name: "File_StorageName", table: "LegalDocuments", maxLength: 100, nullable: true);
// ...and for OwnsStoredFiles tables: AddColumn<string>("StorageName", "<table>", maxLength: 100, nullable: true).
```

All new columns are nullable (or default to 1), so existing rows stay valid: `null` instance names route to `default`,
existing entries are version 1 and current. No data migration is required. Use the schema passed to
`ApplyKitStackStorage` if you set one.

## Notes

- On SQLite, `DateTimeOffset` columns are stored as sortable integers (SQLite cannot compare them in SQL).
  Pass the context to `ApplyKitStackStorage(this)` so this is detected.
- `AddFileEntryTracking` (enabled by default) replaces the registered `IFileStorageManager` with a scoped
  decorator, so register providers (or provider factories and the instance source, for runtime instances) before
  calling `AddEntityFrameworkCoreStore` / `AddKitStackStorageDbContext`. Managers returned by
  `IFileStorageManagerProvider` are tracked too.
  Singletons can no longer depend on `IFileStorageManager` when tracking is on.
