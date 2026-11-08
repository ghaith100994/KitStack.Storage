# KitStack.Fakes

In-memory `IFileStorageManager` for tests and local development. It runs the same pipeline as the real
providers (upload policy, content-type resolution, checksums, encryption, image variants) and implements
`IFakeFileStore` so tests can inspect what was stored.

```csharp
services.AddKitStackStorage().AddFake(o => o.MaxFileSizeBytes = 1024);

var fake = provider.GetRequiredService<IFakeFileStore>();
Assert.Single(fake.ListFiles());
```

Since 0.3 everything an application does with named or runtime instances can run in memory:

```csharp
services.AddKitStackStorage()
    .AddFake()                                   // default
    .AddFake("hrm-docs")                         // isolated named instance
    .AddFakeProvider()                           // runtime definitions with ProviderType = "Fake"
    .AddStorageInstanceSource(mySource)
    .AddInMemoryStore();                         // IFileEntryStore: queries, usage, duplicates, versions, transfers

var hrmFiles = provider.GetRequiredKeyedService<IFakeFileStore>("hrm-docs");
```
