# KitStack.Storage

[![Build](https://img.shields.io/badge/build-pending-lightgrey)](https://github.com/your-org/KitStack.Storage/actions)  
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)  
[![.NET 10](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)

KitStack.Storage is a modular .NET library that provides storage abstractions and provider implementations for common storage backends (Local filesystem, Azure Blob Storage, Amazon S3) plus a Fake provider for testing. It keeps abstractions small and dependency-free so application code remains decoupled from provider SDKs, and is designed to be easily extended with new providers.

Why KitStack.Storage?
- Modular, well-scoped abstractions for file storage.
- Provider implementations shipped separately so consumers install only what they need.
- ASP.NET Core integration helpers for `IServiceCollection` and health-check registration.
- Fakes and samples that make testing and local development easy.
- Optional database support for persisting file metadata (EF Core / MongoDB or other stores).
- Primary target: .NET 10.

Features
- `IFileStorageManager` abstraction (create, read, delete, archive/unarchive)
- Provider implementations:
  - Local filesystem
  - Azure Blob Storage
  - Amazon S3
  - Fake provider for tests/dev
- ASP.NET Core DI helpers and health checks
- Pluggable, optional persistence of file metadata
- Test-first and sample-driven design

Provider status
- Implemented / To implement
  - [ ] Google Cloud Storage (KitStack.Storage.Google)
  - [ ] Backblaze B2 (KitStack.Storage.Backblaze)
  - [ ] MinIO adapter (KitStack.Storage.Minio)
  - [ ] SFTP / FTP provider (KitStack.Storage.Sftp)
  - [ ] WebDAV provider (KitStack.Storage.WebDav)
  - [ ] Other cloud or on-prem providers (community contributions welcome)
  - [ ] Local filesystem (KitStack.Storage.Local)
  - [ ] Azure Blob Storage (KitStack.Storage.Azure)
  - [ ] Amazon S3 (KitStack.Storage.Amazon)
  - [ ] Fake provider (KitStack.Fakes)
  - [ ] ASP.NET Core integration & health checks (KitStack.AspNetCore)
  - Databases / persistence options to standardize and implement:
    - [ ] EF Core (relational): SQL Server (MSSQL)
    - [ ] EF Core (relational): PostgreSQL
    - [ ] EF Core (relational): MySQL
    - [ ] EF Core (relational): SQLite (lightweight/local)
    - [ ] Document store: MongoDB
    - [ ] Other stores / adapters (CosmosDB, Cassandra, etc.)

Database support (optional)
- Purpose: persist file metadata (FileEntry, related entities, audit info) in a database alongside your storage provider.
- Supported approaches (select and enable as needed):
  - EF Core (relational): SQL Server, PostgreSQL, MySQL, SQLite
  - Document stores: MongoDB
  - Custom stores/adapters: CosmosDB, Cassandra, etc. (community/extension)
- Notes:
  - DB support is optional — storage providers (Local/Azure/Amazon) work without a DB.
  - The repo includes a sample StorageContext and module wiring to help you register a DB module when needed.
  - When enabling DB support, register the DB module and run migrations or create indexes as required by the chosen store.
 
Configuration (single appsettings.json)
- Storage and DB configuration live together under `Storage` to simplify binding and deployment.

Example appsettings.json
```json
{
  "Storage": {
    "Provider": "Local",
    "MasterEncryptionKey": "+2ZC9wrwlvPswPxCND0BjrKJ3CfOpImGtn4hloVwo2I=",
    "Local": {
      "Path": "Files"
    },
    "Azure": {
      "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=xxx;AccountKey=xxx;EndpointSuffix=core.windows.net",
      "Container": "test",
      "Path": "app/"
    },
    "Amazon": {
      "AccessKeyID": "",
      "SecretAccessKey": "",
      "BucketName": "",
      "RegionEndpoint": ""
    },
    "Database": {
      "Provider": "mongo",
      "ConnectionString": "mongodb://localhost:27017",
      "DatabaseName": "FileEntries"
    }
  }
}
```

Notes on `Storage:Database` values
- MongoDB: `"Provider": "mongo"`, use `ConnectionString` and `DatabaseName`.
- SQL Server (EF Core): `"Provider": "mssql"`, use `ConnectionString`.
- The repo wiring helper will read `Storage:Database` and register the appropriate DB provider for persisting file metadata.

Startup / Program (example)
```csharp
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

// Bind storage options
services.Configure<StorageOptions>(config.GetSection("Storage"));
services.Configure<LocalOptions>(config.GetSection("Storage:Local"));

// Bind Storage.Database settings
services.Configure<StorageDbOptions>(config.GetSection("Storage:Database"));

// Repo helper: registers DB context (EF Core or Mongo) for StorageContext based on StorageDbOptions
services.BindSeparateDbContext<StorageContext, StorageDbOptions>();

// Register storage manager (choose provider via Storage:Provider)
services.AddKitStackStorage(config.GetSection("Storage"));

var app = builder.Build();
app.UseKitStackStorage(config); // optional middleware/static file setup
app.Run();
```

Security
- Never commit production secrets to source control.
- Use environment variables, user secrets, or a secrets manager for connection strings and cloud credentials.

Repository layout
- src/
  - KitStack.Abstractions/         (IFileStorageManager, IFileEntry, StorageOptions, StorageDbOptions)
  - KitStack.Storage/              (shared helpers and models)
  - KitStack.Storage.Local/        (LocalFileStorageManager, LocalOptions, Local health-check)
  - KitStack.Storage.Azure/        (AzureBlobStorageManager, Azure options, health-check)
  - KitStack.Storage.Amazon/       (AmazonS3StorageManager, Amazon options, health-check)
  - KitStack.Fakes/                (Fake storage manager for tests)
  - KitStack.AspNetCore/           (IServiceCollection extensions, BindSeparateDbContext helper, health-check registration)
  - KitStack.Samples/              (sample console and web apps, DB-backed examples)
- tests/
  - unit and integration tests
- README.md
- LICENSE
- .gitignore

Contributing
1. Fork and create a branch.
2. Implement features or fixes with tests.
3. Open a PR following repository guidelines.
4. Add documentation in `docs/` and update samples as needed.

License
This project is licensed under the MIT License — see the [LICENSE](./LICENSE) file for details.

Maintainer
- ghaith100994 (initial author)
