# KitStack.AspNetCore

KitStack.AspNetCore contains ASP.NET Core integration helpers for the KitStack storage providers.

This package provides utilities for serving files, registering fake/in-memory storage for testing,
and wiring up the local filesystem storage implementation when running in development.

Usage
-----

- Register the desired storage provider in `Startup` / `Program`.
- Use the `IFileStorageManager` abstraction to upload and retrieve files.

For full documentation and examples, see the project repository:
https://github.com/ghaith100994/KitStack.Storage

License
-------
Apache-2.0
