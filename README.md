# KitStack.Storage

[![CI](https://github.com/ghaith100994/KitStack.Storage/actions/workflows/ci.yml/badge.svg)](https://github.com/ghaith100994/KitStack.Storage/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](./LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)

KitStack.Storage is a modular .NET library for file storage: one `IFileStorageManager` abstraction, provider
packages for the local filesystem, Amazon S3, Azure Blob Storage, SFTP and FTP, an in-memory fake for tests,
an EF Core metadata store, and ASP.NET Core integration. Applications only reference the SDKs they use.

## Packages

| Package | Contents |
|---|---|
| `KitStack.Abstractions` | `IFileStorageManager`, `IFileEntry`, `StoredFileRef`, `IFileEntryStore`, upload policy, encryption at rest, the shared provider pipeline and `AddKitStackStorage` |
| `KitStack.Storage.Local` | Local filesystem provider |
| `KitStack.Storage.S3` | Amazon S3 / S3-compatible provider (presigned URLs, per-variant buckets, SSE) |
| `KitStack.Storage.Azure` | Azure Blob provider (SAS URLs, access tiers) |
| `KitStack.Storage.Sftp` | SFTP provider (SSH.NET, password or key auth, host-key pinning) |
| `KitStack.Storage.Ftp` | FTP / FTPS provider (FluentFTP) |
| `KitStack.Storage.EntityFrameworkCore` | EF Core `IFileEntryStore`, `StoredFileRef` mapping, link/unlink interceptor |
| `KitStack.Storage.Mongo` | MongoDB `IFileEntryStore` |
| `KitStack.Fakes` | In-memory provider, provider factory and `IFileEntryStore` for tests |
| `KitStack.AspNetCore` | Authorised file-download endpoint (no provider dependencies) |

## Quick start

```csharp
builder.Services.AddKitStackStorage(builder.Configuration)   // Storage section: policy, encryption
    .AddLocal();                                              // or AddS3(), AddAzureBlob(), AddSftp(), AddFtp(), AddFake()

app.MapKitStackStorageFiles("/files");                         // KitStack.AspNetCore, requires authorization
```

```csharp
// Write: from a form or from any stream (background jobs, imports).
var entry = await storage.CreateAsync<Invoice>(formFile, "Billing");
var report = await storage.CreateAsync<Invoice>(pdfStream, "report.pdf", "application/pdf", "Billing");

// Read, check, delete, link: the same for every provider.
await using var stream = await storage.OpenReadAsync(entry);
return File(stream, entry.ContentType);

bool exists = await storage.ExistsAsync(entry);
Uri? url = await storage.GetUrlAsync(entry, TimeSpan.FromMinutes(10));    // presigned / SAS / PublicBaseUrl
var updated = await storage.ReplaceAsync<Invoice>(entry, newStream, "v2.pdf", null, "Billing");
await storage.OverwriteAsync(updated, restoredStream);                     // same location, e.g. restoring a backup
await storage.DeleteAsync(updated);                                        // also deletes image variants
```

Every stored entry carries a `ContentType` resolved from the file's bytes (the client's claim is recorded but
not trusted) and a SHA-256 `Checksum` computed while the file is written.

Choosing the provider from configuration:

```csharp
var storage = builder.Services.AddKitStackStorage(builder.Configuration);
if (storage.IsProvider("S3")) storage.AddS3();
else if (storage.IsProvider("Azure")) storage.AddAzureBlob();
else storage.AddLocal();
```

## Configuration

```json
{
  "Storage": {
    "Provider": "Local",
    "MasterEncryptionKey": "<base64 32-byte key from a secret store>",
    "Upload": {
      "MaxBytes": 26214400,
      "AllowedContentTypes": [ "image/*", "application/pdf" ],
      "AllowedExtensions": [ ".jpg", ".png", ".pdf" ]
    },
    "Local": { "Path": "Files", "PublicBaseUrl": "https://app.example.com/files" },
    "S3": { "MainTarget": { "BucketName": "files", "Region": "eu-west-1" } },
    "Azure": { "ConnectionString": "...", "ContainerName": "files" },
    "Sftp": { "Host": "sftp.example.com", "Username": "app", "PrivateKeyPath": "/run/secrets/key", "HostKeyFingerprint": "SHA256:..." },
    "Ftp": { "Host": "ftp.example.com", "UseSsl": true }
  }
}
```

- **Encryption at rest**: when `MasterEncryptionKey` is set, every new file (and image variant) is encrypted with
  AES-256-GCM using a per-file key derived with HKDF. Reads decrypt transparently; `GetUrlAsync` returns `null` for
  encrypted files because a direct URL would serve ciphertext. Move old keys to `PreviousEncryptionKeys` to rotate.
  Generate a key with `openssl rand -base64 32` and keep it in a secret store, never in `appsettings.json`.
- **Upload policy** is enforced before any provider is called; violations throw `StorageValidationException`.

## Named and dynamic instances

One application can use several storage instances at once, each with its own provider, settings, upload policy,
encryption key and credentials. The instance registered without a name is `default` and is still the unkeyed
`IFileStorageManager`, so 0.2 code keeps working unchanged.

### Static instances (configuration)

```csharp
builder.Services.AddKitStackStorage(builder.Configuration)
    .AddLocal()                                                        // "default"
    .AddLocal("public-assets", o => o.Path = "wwwroot/assets")
    .AddS3("hrm-docs", o => { o.MainTarget.BucketName = "erp-private"; o.MainTarget.Prefix = "hrm/employees/documents"; })
    .ConfigureInstance("hrm-docs", o => o.Upload = new UploadPolicy { MaxBytes = 10_000_000 });
```

Named instances bind from `Storage:Instances:{name}:{Provider}` (provider options) and `Storage:Instances:{name}`
(`Upload`, `MasterEncryptionKey`, `PreviousEncryptionKeys`, `DisableEncryption`, overriding the global values):

```json
{ "Storage": { "Instances": { "hrm-docs": { "S3": { "MainTarget": { "BucketName": "erp-private" } }, "Upload": { "MaxBytes": 10000000 } } } } }
```

Resolve them through `IFileStorageManagerProvider` or as keyed services:

```csharp
var hrm = provider.Get("hrm-docs");                       // or [FromKeyedServices("hrm-docs")] IFileStorageManager
var entry = await hrm.CreateAsync(new StorageWriteOptions
{
    FileName = upload.FileName,
    Category = "HRM",
    Folder = "employees/documents",                       // explicit folder (sanitised, no traversal)
    Policy = new UploadPolicy { MaxBytes = 2_000_000 },   // per call: can only be stricter
    Link = new StorageEntityLink { EntityName = "Employee", EntityId = employeeId.ToString() },
}, upload.OpenReadStream());

await using var stream = await provider.Get(entry).OpenReadAsync(entry);   // routed by entry.StorageName (null = default)
```

### Runtime instances (database rows)

Implement `IStorageInstanceSource` over your own table and register the provider factories the rows may use.
Instances are built on first use, cached, rebuilt when `Version` changes, and replaced builds are disposed once
their in-flight operations and open read streams have finished.

```csharp
public sealed class StorageNodeSource(IDbContextFactory<AdminDbContext> db) : IStorageInstanceSource
{
    public async ValueTask<StorageInstanceDefinition?> FindAsync(string name, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var node = await context.StorageNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Name == name && n.Enabled, ct);
        if (node is null)
            return null;

        return new StorageInstanceDefinition(node.Name, node.ProviderType)     // "Local", "S3", "Azure", "Sftp", "Ftp", "Fake"
        {
            Version = node.RowVersion,                                          // change detection
            Settings = StorageInstanceDefinition.CreateSettings(node.Settings), // e.g. "MainTarget:BucketName" = "erp-private"
            Storage = new StorageOptionsOverrides { Upload = node.ToUploadPolicy(), DisableEncryption = !node.Encrypt },
            SecretReferences = new Dictionary<string, string>
            {
                [StorageSecretFields.S3AccessKeyId] = $"{node.Name}-access-key",
                [StorageSecretFields.S3SecretAccessKey] = $"{node.Name}-secret-key",
                [StorageSecretFields.MasterEncryptionKey] = $"{node.Name}-encryption-key",
            },
        };
    }
}

builder.Services.AddKitStackStorage(builder.Configuration)
    .AddLocalProvider().AddS3Provider().AddAzureBlobProvider()
    .AddStorageInstanceSource<StorageNodeSource>(o => o.RefreshInterval = TimeSpan.FromMinutes(1))
    .AddEntityFrameworkCoreStore<AppDbContext>();          // tracks files of every instance

// After an administrator edits or disables a node:
provider.Invalidate("HRM-Employee-Documents");
```

- **Secrets by reference**: `SecretReferences` maps setting paths to references resolved by `IStorageSecretResolver`
  when the instance is built. The default resolver reads `StorageSecrets:{reference}` from configuration (environment
  variables such as `StorageSecrets__hrm-secret-key`, user secrets, Key Vault, ...). Resolved values are never logged
  and are redacted from exception messages and health descriptions.
- **Test connection** before saving a node: `IStorageInstanceValidator.TestAsync(definition)` validates the provider
  options (DataAnnotations / `IValidatableObject`), the storage options and the secrets, then probes connectivity with
  the provider's health-check logic. Nothing is cached.
- **Health**: `IStorageHealthProbe.CheckAsync(name)` for any instance; `AddKitStackStorageHealthChecks()` registers
  `kitstack_storage_{name}` for every static instance (the provider checks such as `kitstack_s3` remain for `default`).
- **Capabilities**: `provider.GetCapabilities(name)` (`SignedUrls`, `PublicUrls`, `Overwrite`, `AccessTiers`,
  `ServerSideEncryption`, `ImageVariantRouting`); encrypted instances never report URL capabilities.

### Moving files, versions and reporting

```csharp
// Move a file (and its variants) to another instance: decrypted, re-encrypted with the target key, verified by
// checksum, metadata re-pointed with the same Id and links, then optionally deleted from the source. Re-runnable.
var result = await transfers.TransferAsync(entry, "Archiving-Documents", new FileTransferOptions { DeleteSource = true });

// Keep history when replacing a file.
var v2 = await versions.ReplaceAsync(current, options, stream, new ReplaceOptions { KeepPreviousVersion = true });
await versions.RestoreVersionAsync(v1.Id);      // creates a new current version from the old bytes
await versions.PurgeVersionsAsync(v2.Id, keepLatest: 3);

// Reporting through the metadata store (EF Core, MongoDB and the in-memory store).
var page = await store.QueryAsync(new FileEntryQuery { StorageName = "hrm-docs", ContentTypePrefix = "image/", Take = 50 });
var usage = await store.GetUsageAsync(new FileUsageQuery { GroupBy = FileUsageGrouping.StorageName | FileUsageGrouping.Category });
var duplicates = await store.FindDuplicatesAsync("hrm-docs", category: null);
```

`StoredFileRef` copies an entry's location: after a transfer, resolve references by `FileEntryId`
(`IFileEntryStore.FindManyAsync`) and refresh them.

## File metadata and references

`KitStack.Storage.EntityFrameworkCore` records uploads, links files to entities through `StoredFileRef` owned
properties, releases links when references are replaced or owners are deleted, and can sweep orphaned files.
See [its README](src/KitStack.Storage.EntityFrameworkCore/README.md).

```csharp
public class LegalDocument
{
    public Guid Id { get; set; }
    public StoredFileRef? File { get; set; }   // FileEntryId, Location, ContentType, Size, Checksum, ...
}
```

## Upgrading

0.3 is additive; see [CHANGELOG.md](CHANGELOG.md) for the new database columns (EF Core migration) and MongoDB
fields. For 0.1 → 0.2 see the same file for the breaking changes (the `Sftp` → `Ftp` rename, `AddKitStackStorage`
moving to `KitStack.Abstractions`, anonymous static files becoming opt-in, and encryption now being real).

## Building

```bash
dotnet build
dotnet test
```

ImageSharp 4 validates a Six Labors license key in **Release** builds. Set the `SIXLABORS_LICENSE_KEY`
environment variable (CI reads it from the repository secret of the same name). Debug builds only warn.

## License

Apache-2.0, see [LICENSE](./LICENSE).
