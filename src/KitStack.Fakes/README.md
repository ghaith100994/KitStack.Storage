# KitStack.Fakes

KitStack.Fakes contains an in-memory fake storage provider used for unit tests and local development.

This package implements `IFileStorageManager` without external dependencies by keeping file content in memory and exposes `IFakeFileStore` so tests can inspect and manipulate the stored state.

Usage
-----

- Register the fake provider using `AddInMemoryFakeStorage` in test or development DI setups.
- Use `IFakeFileStore` in tests to assert stored files or clear the store between test runs.

For full documentation and examples, see the project repository:
https://github.com/ghaith100994/KitStack.Storage

License
-------
Apache-2.0
