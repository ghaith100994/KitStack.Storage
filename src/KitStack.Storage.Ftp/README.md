# KitStack.Storage.Ftp

FTP / FTPS storage provider for KitStack.Storage, built on FluentFTP.

> Before 0.2 this provider was published as `KitStack.Storage.Sftp` even though it only spoke FTP.
> If you used it, switch to this package and rename the configuration section from `Storage:Sftp` to
> `Storage:Ftp`. `KitStack.Storage.Sftp` is now a real SFTP (SSH) provider.

```json
"Storage": {
  "Ftp": {
    "Host": "ftp.example.com",
    "Port": 21,
    "Username": "user",
    "Password": "secret",
    "UseSsl": true,
    "RemotePath": "Files"
  }
}
```

```csharp
builder.Services.AddKitStackStorage(builder.Configuration).AddFtp();
```

`UseSsl` enables explicit FTPS (AUTH TLS) for control and data connections; without it credentials and
content travel in clear text. `ValidateAnyCertificate` disables certificate validation and is for testing only.

The provider keeps one connection and serializes operations on it. Reads download the whole file to a
temp buffer before returning the stream.
