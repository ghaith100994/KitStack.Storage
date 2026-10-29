# Changelog

## 0.2.0

### Added

- **Read, delete and URL APIs** on `IFileStorageManager`: `OpenReadAsync`, `ExistsAsync`, `DeleteAsync`
  (also deletes image variants recorded on the original), `GetUrlAsync` (S3 presigned URLs, Azure SAS URLs,
  Local `PublicBaseUrl`). Implemented by every provider.
- **Stream input**: `CreateAsync<T>(Stream, fileName, contentType, category)` and a stream overload of
  `CreateWithVariantsAsync`, so background jobs and imports no longer need a fake `IFormFile`.
- **`IFileEntry.Checksum`**: SHA-256 of the content, computed while the file is written.
- **Resolved `IFileEntry.ContentType`**: detected from the file signature plus extension. The client's declared
  type is stored in `Metadata["DeclaredContentType"]` but never trusted; a file whose bytes do not match a binary
  extension (for example HTML named `.jpg`) resolves to `application/octet-stream`.
- **`UploadPolicy`** (`Storage:Upload`): `MaxBytes`, `AllowedContentTypes` (wildcards like `image/*`),
  `AllowedExtensions`, enforced before the provider is called.
- **`StoredFileRef`** value object, plus `OpenReadAsync` / `ExistsAsync` / `DeleteAsync` / `GetUrlAsync` overloads.
- **`OverwriteAsync`**: replace the bytes at an entry's existing location (restoring a backup, saving an edit in
  place) on every provider, with the same size limit, content-type resolution, checksum and encryption as uploads.
- **`ReplaceAsync`** helper: stores the new file, then deletes the old one.
- **`IFileEntryStore`** contract, `FileEntryTrackingStorageManager` decorator (`AddFileEntryTracking()`) and
  `FileOrphanSweeper`.
- **`KitStack.Storage.EntityFrameworkCore`**: EF Core `IFileEntryStore`, `ApplyKitStackStorage` / `KitStackStorageDbContext`,
  `OwnsStoredFile` / `OwnsStoredFiles`, `FileReferenceInterceptor` (link on set, unlink on replace/clear/delete)
  and `IFileEntryStore.UpdateAsync`.
- **`KitStack.Storage.Mongo`**: MongoDB `IFileEntryStore` (`AddMongoStore()`, configured from `Storage:Database`).
- Opt-in `AddOrphanSweeper()` hosted service (in `KitStack.Abstractions`), working with any `IFileEntryStore`.
- **`KitStack.Storage.Azure`**: Azure Blob provider (connection string, service URI or DI-registered
  `BlobServiceClient`; SAS and user-delegation SAS URLs; upload tiers and `SetAccessTierAsync`).
- **`KitStack.Storage.Sftp`** is now a real SFTP provider on SSH.NET (password or private key, host-key pinning).
- **`MapKitStackStorageFiles`** endpoint in `KitStack.AspNetCore`: serves files from any provider through
  `OpenReadAsync`, requires authorization by default, supports a per-file `Authorize` callback, ranges and ETags,
  and sends active content (HTML, SVG, ...) as attachments with `nosniff` and a sandbox CSP.
- `KitStackStorageBuilder` with `AddLocal()`, `AddS3()`, `AddAzureBlob()`, `AddSftp()`, `AddFtp()`, `AddFake()`,
  each registering its health check (`kitstack_*`).
- Test project (`tests/KitStack.Storage.Tests`) and a CI workflow.

### Changed (behaviour)

- **Encryption is real.** `Storage:MasterEncryptionKey` used to do nothing. When it is set, new files are now
  encrypted at rest with AES-256-GCM and `IFileEntry.Encrypted` is `true`. Files written before remain readable.
  If you had a key configured only because the old README showed one, remove it or keep it in a secret store:
  losing the key makes the files unreadable.
- **Anonymous static files are off by default.** `UseKitStackStorage` now only serves the Local directory when
  `Storage:Local:ServeAnonymously` is `true`. Use `MapKitStackStorageFiles` instead.
- **`KitStack.Storage.Sftp` → `KitStack.Storage.Ftp`.** The old package spoke plain FTP. Its code now lives in
  `KitStack.Storage.Ftp` (`AddFtp()`, section `Storage:Ftp`, `FtpOptions`, `FtpFileStorageManager`).
  `KitStack.Storage.Sftp` is a different, SSH-based provider (default port 22, host key must be verified).
- FTP: `UseSsl` and `ValidateAnyCertificate` used to be ignored; they now enable explicit FTPS.
- S3: `UseServerSideEncryption: true` without `KmsKeyId` used to do nothing; it now applies SSE-S3 (AES256).
- File and folder names are sanitized consistently across providers (letters, including non-Latin letters, digits,
  `-`, `_`, `.`); categories containing `.` or `..` segments are rejected.
- New file ids are version 7 GUIDs (time-ordered, index-friendly).

### Breaking (API)

- `AddKitStackStorage` moved from `KitStack.AspNetCore` to `KitStack.Abstractions`
  (`KitStack.Abstractions.DependencyInjection`) and returns a `KitStackStorageBuilder`. `KitStack.AspNetCore` no
  longer references provider packages: add the provider package and call its builder method, e.g.
  `services.AddKitStackStorage(configuration).AddLocal()`.
- `UseKitStackStorageEnsureBucketsAsync` was removed (it could never resolve the S3 manager). Use
  `app.Services.EnsureS3BucketsExistAsync()` from `KitStack.Storage.S3`.
- `IFileEntry.ContentType` is now `string` (non-nullable, defaults to `application/octet-stream`).
  `IFileEntry.Checksum` has a default implementation, so existing implementations keep compiling.
- `IFileStorageManager` has new members; custom implementations must add them (or derive from
  `FileStorageManagerBase`).
- `FileStorageManagerBase`: providers now implement `WriteCoreAsync`, `OpenReadCoreAsync`, `ExistsCoreAsync`,
  `DeleteCoreAsync` and optionally `GetUrlCoreAsync` instead of `CreateAsync` + `StoreVariantAsync`.
- `S3FileStorageManager` and `S3HealthCheck` take `IOptions<S3Options>` (the 0.1 constructors could not be
  resolved from DI).
- `BindSeparateDbContextOptions` is obsolete; use `KitStack.Storage.EntityFrameworkCore`.

### Fixed (found by the 0.2 test suite)

- Azure: uploads without `AccessTier` configured threw `ArgumentNullException` (a `null` tier went through
  `AccessTier`'s implicit string conversion).
- FTP: anonymous FTP (no `Username`) could not connect; FluentFTP rejects empty user names, so it now logs in
  as `anonymous`.
- `S3PresignedUrlGenerator`: every call for the main bucket threw `NullReferenceException` (the client was read
  before it was assigned), and a call disposed the shared client.
- `OwnsStoredFiles`: saving failed on SQLite because the owned table's composite key could not be generated; the
  table now has its own identity key.
- `AddFileEntryTracking()` called twice (or after a store method that already enabled it) wrapped the decorator
  around itself and recursed forever on resolve; it is now idempotent.
- `StoragePaths.SanitizeCategory` did not split on `\` (wrong `Split` overload).

### Dependencies

- SixLabors.ImageSharp 4.1.3 (Release builds need `SIXLABORS_LICENSE_KEY`), Microsoft.Extensions.* 10.0.14,
  AWSSDK.S3 4.0.104.6, FluentFTP 55.1.0; new: Azure.Storage.Blobs 12.30.1, SSH.NET 2026.0.0,
  Microsoft.EntityFrameworkCore.Relational 10.0.14, MongoDB.Driver 3.13.0.
