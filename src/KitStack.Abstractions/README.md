# KitStack.Abstractions

KitStack.Abstractions contains lightweight interfaces, DTOs and utilities for the KitStack storage providers.

This package provides the core abstractions used across provider implementations (for example `IFileStorageManager`, `IFileEntry`) and small concrete DTOs useful for samples and tests.

Usage
-----

- Reference the project or package from provider or application projects that need to implement or consume storage abstractions.
- Use the `IFileStorageManager` abstraction in application code to remain provider-agnostic.

For full documentation and examples, see the project repository:
https://github.com/ghaith100994/KitStack.Storage

License
-------
Apache-2.0


