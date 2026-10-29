# KitStack.Storage.Mongo

MongoDB metadata store for KitStack.Storage. It implements `IFileEntryStore`: one document per stored file
(collection `fileEntries`), with its entity links embedded, so files can be found by id, location or owning
entity, and orphans can be swept.

```json
"Storage": {
  "Database": { "ConnectionString": "mongodb://localhost:27017", "DatabaseName": "Files" }
}
```

```csharp
builder.Services.AddKitStackStorage(builder.Configuration)
    .AddLocal()          // provider first
    .AddMongoStore();    // records every upload; links via CreateAsync(entity, ...) or IFileEntryStore.LinkAsync
```

To reuse an existing client: `.AddMongoStore(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("files"))`.

The orphan sweeper works the same as with EF Core: `.AddOrphanSweeper()` (opt-in, see the root README).
Unlike the EF Core package there is no automatic link/unlink interceptor: link files with
`IFileEntryStore.LinkAsync` / `UnlinkAsync` where your documents reference them.

Indexes (`FileLocation`, `ParentId`, `IsDeleted + UploadedTime`, `Links.EntityName + Links.EntityId`) are
created on first use. Guids are stored as standard UUIDs and times as UTC BSON dates.
