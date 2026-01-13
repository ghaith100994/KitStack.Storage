# KitStack.Storage.S3

KitStack.Storage.S3 provides an Amazon S3 storage provider for KitStack.Storage.

This package implements:

- Upload/download via AWS S3
- Image variant creation (thumbnails, compressed, additional sizes)
- Health checks for bucket access
- Presigned URL generation helpers

Usage
-----

- Configure `Storage:S3` in your application configuration and call `services.AddS3StorageManager(configuration.GetSection("S3"))` or use the `AddKitStackStorage` helper from `KitStack.AspNetCore` when `Storage:Provider` is set to `s3`.
- Use the `IFileStorageManager` abstraction to upload and retrieve files.

For full documentation and examples, see the project repository:
https://github.com/ghaith100994/KitStack.Storage

License
-------
Apache-2.0
