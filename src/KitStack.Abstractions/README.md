# KitStack.Abstractions

Interfaces, models and the shared provider pipeline for KitStack.Storage.

- `IFileStorageManager`: `CreateAsync` (form file or stream), `CreateWithVariantsAsync`, `OpenReadAsync`,
  `ExistsAsync`, `DeleteAsync`, `GetUrlAsync`. Helpers: `ReplaceAsync`, and `StoredFileRef` overloads.
- `IFileEntry` with a resolved `ContentType` and a SHA-256 `Checksum`.
- `StoredFileRef`: value object entities store instead of repeating file columns.
- `IFileEntryStore`: metadata store contract (EF Core implementation in KitStack.Storage.EntityFrameworkCore).
- `UploadPolicy` (`Storage:Upload`): `MaxBytes`, `AllowedContentTypes`, `AllowedExtensions`.
- Encryption at rest: set `Storage:MasterEncryptionKey` (base64, 32 bytes) to encrypt every new file with
  AES-256-GCM; `Storage:PreviousEncryptionKeys` keeps old keys readable after rotation.
- `FileStorageManagerBase`: providers implement five primitives and inherit validation, the policy,
  content-type resolution, checksums, encryption and image variants.
- `AddKitStackStorage(...)` returns a builder that provider packages extend (`AddLocal()`, `AddS3()`, ...).
