# KitStack.Fakes

In-memory `IFileStorageManager` for tests and local development. It runs the same pipeline as the real
providers (upload policy, content-type resolution, checksums, encryption, image variants) and implements
`IFakeFileStore` so tests can inspect what was stored.

```csharp
services.AddKitStackStorage().AddFake(o => o.MaxFileSizeBytes = 1024);

var fake = provider.GetRequiredService<IFakeFileStore>();
Assert.Single(fake.ListFiles());
```
