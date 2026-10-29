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
| `KitStack.Fakes` | In-memory provider for tests |
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

## Upgrading from 0.1

See [CHANGELOG.md](CHANGELOG.md) for the breaking changes (the `Sftp` → `Ftp` rename, `AddKitStackStorage`
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
