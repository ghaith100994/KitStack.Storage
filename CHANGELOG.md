# Changelog

## 0.3.0

Every package versions together at 0.3.0. Apart from the package rename below, the changes are additive: new
interface members have default implementations, and 0.2 behaviour is unchanged.

### Breaking: packages and namespaces renamed

Every package now lives under `KitStack.Storage`:

| 0.2 package / namespace | 0.3 package / namespace |
|---|---|
| `KitStack.Abstractions` | `KitStack.Storage.Abstractions` |
| `KitStack.Fakes` | `KitStack.Storage.Fakes` |
| `KitStack.AspNetCore` | `KitStack.Storage.AspNetCore` |

Replace the package references and change `using KitStack.Abstractions...` to `using KitStack.Storage.Abstractions...`
(likewise for `Fakes` and `AspNetCore`). Type names, members and configuration sections are unchanged, so the rename is a
search-and-replace. The provider and store packages (`KitStack.Storage.Local`, `.S3`, `.Azure`, `.Sftp`, `.Ftp`,
`.EntityFrameworkCore`, `.Mongo`) keep their package ids. The 0.2 packages on nuget.org should be marked deprecated
with the new package as the alternative.

### Added

- **Named storage instances.** `StorageInstance` names (`default` for the unnamed registration, still resolvable as the
  unkeyed `IFileStorageManager`). Provider packages gain named overloads (`AddLocal("name", ...)`, `AddS3("name", ...)`,
  `AddAzureBlob`, `AddSftp`, `AddFtp`, `AddFake`), bound from `Storage:Instances:{name}:{Provider}` with named options;
  a second named `AddS3` no longer overwrites the first. Named instances are also keyed services.
- **`IFileStorageManagerProvider`** (singleton): `Get(name)`, `GetAsync`, `TryGet`, `Get(IFileEntry)`,
  `Get(StoredFileRef)` (routing by `StorageName`, `null` = `default`), `Names`, `GetCapabilities`, `Invalidate`,
  `InvalidateAll`.
- **Runtime instances.** `IStorageInstanceSource` + `StorageInstanceDefinition` (`Name`, `ProviderType`, `Settings` bound
  to the provider's options type, `Storage` overrides, `SecretReferences`, `Version`). Built on first use, cached,
  re-checked after `StorageInstanceCacheOptions.RefreshInterval`, rebuilt when `Version` changes; replaced and evicted
  builds are disposed exactly once after their in-flight operations and open read streams complete. Stale manager
  references forward to the current build.
- **`IStorageProviderFactory`** per provider package (`AddLocalProvider()`, `AddS3Provider()`, `AddAzureBlobProvider()`,
  `AddSftpProvider()`, `AddFtpProvider()`, `AddFakeProvider()`, also registered by the existing `AddXxx()` calls) and the
  `StorageProviderFactory<TOptions>` base class.
- **`IStorageInstanceValidator.TestAsync`**: validates a definition and probes connectivity without caching it.
- **Secrets by reference**: `IStorageSecretResolver` (default: `StorageSecrets:{reference}` from configuration),
  `StorageSecretFields` for the well-known fields (S3 keys, Azure connection string, SFTP/FTP password and key, encryption
  keys). Resolved values are never logged and are redacted from exceptions, validation errors and health descriptions.
- **Per-instance storage options** (`StorageOptionsOverrides`: upload policy, master/previous encryption keys,
  `DisableEncryption`) merged over the global options; `ConfigureInstance(name, ...)`. Instances without a master key
  still read files encrypted with their previous keys.
- **Non-generic write API**: `CreateAsync(StorageWriteOptions, Stream)` and `CreateWithVariantsAsync(StorageWriteOptions, Stream)`
  with `EntityName`, explicit sanitised `Folder` (absolute paths and `.`/`..` segments rejected), a per-call `Policy`
  that can only tighten the instance policy, `Metadata` and an entity `Link`. The generic overloads delegate to it.
  Entries record their logical folder in `Metadata["Folder"]`.
- **Instance identity and versions on entries**: `IFileEntry.StorageName`, `PreviousVersionId`, `VersionNumber`,
  `SupersededById` (default-implemented), on `FileEntry`, the EF and Mongo records; `StoredFileRef.StorageName`.
- **`IFileTransferService.TransferAsync`**: moves a file and its variants between instances (decrypt, re-encrypt with
  the target keys, keep folder/name/metadata, verify checksums, re-point metadata atomically with the same `Id` and
  links, optionally delete the source). Idempotent and resumable; returns per-step status.
- **`IFileVersionService`**: `ReplaceAsync` with `ReplaceOptions.KeepPreviousVersion`, `ListVersionsAsync`,
  `RestoreVersionAsync` (creates a new version from old bytes), `PurgeVersionsAsync`.
- **`IFileEntryStore`**: `QueryAsync` (`FileEntryQuery` → `FileEntryPage`), `GetUsageAsync` (grouped in the database),
  `FindDuplicatesAsync` (per instance unless `AcrossStorageInstances`), `FindManyAsync`, `RelocateAsync`,
  `SupersedeAsync`, `ListVersionsAsync`, `FindByLocationAsync(location, storageName)` and
  `ListOrphansAsync(..., storageName, includeSupersededVersions)`. Implemented natively by the EF Core, MongoDB and
  new in-memory stores.
- **`IStorageContentAccess`** (raw put/verify/delete of bytes, location prediction) implemented by every provider.
- **Health and capabilities**: `IStorageHealthProbe.CheckAsync(name)` reusing each provider's health check per instance,
  `AddKitStackStorageHealthChecks()` (`kitstack_storage_{name}`), `StorageCapabilities` per provider type and instance.
- **S3**: `IS3ClientFactory` to supply clients; `EnsureS3BucketsExistAsync(storageName)`.
- **KitStack.Storage.Fakes**: `AddFake("name")` (keyed `IFakeFileStore`), `FakeStorageProviderFactory`, `InMemoryFileEntryStore`
  and `AddInMemoryStore()`.
- `FileStorageManagerExtensions.Unwrap<T>()`, non-generic `ReplaceAsync(existing, options, content)`.

### Changed

- `FileEntryTrackingStorageManager`, `FileOrphanSweeper`, `FileOrphanSweeperHostedService` and `MapKitStackStorageFiles`
  route each operation to the instance that owns the entry. The sweeper isolates failing instances, accepts a
  `storageName` (`SweepAsync(minAge, batchSize, storageName)`, `OrphanSweeperOptions.StorageNames`) and never deletes
  previous versions kept by `KeepPreviousVersion`. `MapKitStackStorageFiles` accepts `?storage={name}` on location URLs.
- `AddFileEntryTracking()` no longer requires an unnamed provider when provider factories, named instances or an instance
  source are registered.
- `FileOrphanSweeper` has a single constructor `(store, storage = null, logger = null, provider = null)` (source compatible).
- Entries created by every provider carry `StorageName` (`default` for the unnamed instance) and `Metadata["Folder"]`.

- The publish workflow uses NuGet Trusted Publishing (`NuGet/login`, GitHub OIDC) instead of a stored
  `NUGET_API_KEY`: it needs a trusted publishing policy on the nuget.org account and no secret.

### Migration notes

- **EF Core**: add a migration. New nullable `FileEntries.StorageName` (max 100), `PreviousVersionId`, `SupersededById`,
  `VersionNumber` (int, default 1), indexes `(StorageName, IsDeleted)`, `(StorageName, Checksum)`, `SupersededById`,
  and a nullable `{Navigation}_StorageName` column for each `OwnsStoredFile` mapping (`StorageName` for `OwnsStoredFiles`
  tables). `null` = `default`; no data migration needed. See the EF Core package README for the migration code.
- **MongoDB**: new optional fields `StorageName`, `PreviousVersionId`, `VersionNumber`, `SupersededById` and indexes
  `(StorageName, IsDeleted)`, `(StorageName, Checksum)`, `SupersededById` (created automatically). Missing fields read as
  `default` / version 1 / current.
- Custom `IFileEntryStore` implementations keep compiling; `QueryAsync`, `GetUsageAsync`, `FindDuplicatesAsync`,
  `RelocateAsync` and `SupersedeAsync` throw `NotSupportedException` until implemented. Their `ListOrphansAsync` should
  exclude entries whose `SupersededById` is set.
- `StoredFileRef` copies a location: after a transfer, refresh references by `FileEntryId` with `FindManyAsync`.

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
