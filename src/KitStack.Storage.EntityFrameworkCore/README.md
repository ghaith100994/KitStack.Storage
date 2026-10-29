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

## Notes

- On SQLite, `DateTimeOffset` columns are stored as sortable integers (SQLite cannot compare them in SQL).
  Pass the context to `ApplyKitStackStorage(this)` so this is detected.
- `AddFileEntryTracking` (enabled by default) replaces the registered `IFileStorageManager` with a scoped
  decorator, so register providers before calling `AddEntityFrameworkCoreStore` / `AddKitStackStorageDbContext`.
  Singletons can no longer depend on `IFileStorageManager` when tracking is on.
