# KitStack.Storage.Local

KitStack.Storage.Local contains the local filesystem storage provider and ASP.NET Core helpers for KitStack storage.

This package stores files on the local filesystem under a configurable base path and can optionally create image variants (compressed, thumbnails, additional sizes) when enabled in options. It also provides a health check that verifies the storage path is writable.

Usage
-----

- Register the provider using `AddLocalStorageManager`.
- Use the `IFileStorageManager` abstraction to upload and retrieve files.

Configuration
-----

Options are bound from configuration. Typical keys (example):

```json
{
  "Path": "Files",
  "EnsureBasePathExists": true,
  "ImageProcessing": {
    "CreateCompressed": true,
    "CompressedMaxWidth": 1200,
    "CompressedMaxHeight": 1200,
    "CreateThumbnail": true,
    "ThumbnailMaxWidth": 600,
    "ThumbnailMaxHeight": 600,
    "JpegQuality": 85
  }
}
```

For full documentation and examples, see the project repository:
https://github.com/ghaith100994/KitStack.Storage

License
-------
Apache-2.0
