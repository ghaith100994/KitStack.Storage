# KitStack.Storage.Tests

Unit and integration tests for every package, one folder per library:

| Folder | Covers | How |
|---|---|---|
| `Abstractions/` | Base pipeline, content-type resolution, paths, encryption, spooling, policy, models, tracking decorator, orphan sweeper, hosted service, DI builder | `RecordingStorageManager` (a minimal provider over a dictionary), NSubstitute for `IFileEntryStore` |
| `Local/` | Local provider, health check, registration | Real filesystem under a temp directory |
| `Fakes/` | In-memory provider | Direct |
| `S3/` | S3 provider, routing, SSE, presigned URLs, options, bucket helper, health check | Mocked `IAmazonS3` through the internal client-factory constructor; the real SDK for offline URL signing |
| `Azure/` | Azure provider, tiers, SAS / user-delegation URLs, health check | Mocked `BlobServiceClient`/`BlobContainerClient`/`BlobClient`; the real SDK with `UseDevelopmentStorage=true` for offline SAS |
| `Ftp/` | FTP provider, serialisation, client factory, options | Mocked `IAsyncFtpClient` through the internal client constructor |
| `Sftp/` | SFTP provider, directory creation, host-key pinning, key auth | Mocked `ISftpClient`; real SSH.NET objects for authentication setup |
| `EntityFrameworkCore/` | EF store, owned types, interceptor (same and separate database, sync and async, failed saves) | SQLite in memory |
| `Mongo/` | Mongo store and registration | A real `mongod` (EphemeralMongo; on linux-x64 the binary bundled in `EphemeralMongo8.runtime.linux-x64`, elsewhere downloaded) |
| `AspNetCore/` | File endpoint (auth, per-file auth, headers, ETag, ranges), static-file opt-in | `TestServer` |
| `Instances/` | 0.3: named and runtime instances (caching, version rebuilds, invalidation, deferred disposal, concurrency), secrets and redaction, test connection, per-instance policy and encryption, explicit folders, transfers, versions, store contract (query, usage, duplicates, relocation) on every store, sweeper and endpoint routing, health probes, capabilities | Fakes, Local, mocked S3 (`IS3ClientFactory`), SQLite, EphemeralMongo, `TestServer`; `FileEntryStoreContractTests` runs the same cases against the EF Core, MongoDB and in-memory stores |

Health checks for S3, FTP and SFTP are exercised against an unreachable local port, so no external service is needed.

```bash
dotnet test                                         # all tests
dotnet test --filter "FullyQualifiedName~.S3."      # one library
dotnet test --collect:"XPlat Code Coverage"         # with coverage (cobertura XML under TestResults/)
```

Tests that count temp files run in the non-parallel `Temp files` collection.
