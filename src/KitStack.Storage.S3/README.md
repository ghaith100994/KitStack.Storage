# KitStack.Storage.S3

KitStack.Storage.S3 provides an Amazon S3 storage provider for KitStack.Storage.

This package implements:

- Upload/download via AWS S3
- Image variant creation (thumbnails, compressed, additional sizes)
- Health checks for bucket access
- Presigned URL generation helpers

Usage
-----

- Configure `Storage:S3` and call `services.AddKitStackStorage(configuration).AddS3()`.
- Use the `IFileStorageManager` abstraction to upload, read, delete and get presigned URLs (`GetUrlAsync`,
  lifetime defaults to `PresignedUrlExpirationSeconds`).
- `UseServerSideEncryption: true` applies SSE-KMS when `KmsKeyId` is set and SSE-S3 (AES256) otherwise.
- `await app.Services.EnsureS3BucketsExistAsync()` creates buckets whose target has `EnsureBucketExists: true`.

For full documentation and examples, see the project repository:
https://github.com/ghaith100994/KitStack.Storage

License
-------
Apache-2.0
