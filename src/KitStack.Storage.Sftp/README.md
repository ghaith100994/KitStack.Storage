# KitStack.Storage.Sftp

SFTP (SSH File Transfer Protocol) storage provider for KitStack.Storage, built on SSH.NET.

> Up to 0.1.x this package spoke plain FTP through FluentFTP. That provider is now
> `KitStack.Storage.Ftp` (configuration section `Storage:Ftp`). This package is a real SFTP provider:
> the default port is 22 and the host key must be verified.

```json
"Storage": {
  "Sftp": {
    "Host": "sftp.example.com",
    "Port": 22,
    "Username": "uploader",
    "PrivateKeyPath": "/run/secrets/sftp_key",
    "HostKeyFingerprint": "SHA256:2l1d0R2PO1d6Vh8Lw7NnS1Y8y3d9g1R7sJ5xP6l0e8A",
    "RemotePath": "Files"
  }
}
```

```csharp
builder.Services.AddKitStackStorage(builder.Configuration).AddSftp();
```

- Authentication: `Password`, or `PrivateKey` (key content) / `PrivateKeyPath` with optional `PrivateKeyPassphrase`.
- Host key: set `HostKeyFingerprint` to the value printed by `ssh-keygen -lf <host key>.pub` (or
  `ssh-keyscan host | ssh-keygen -lf -`). `AcceptAnyHostKey = true` disables verification and is for testing only.
- Missing remote directories are created when `EnsureRemotePathExists` is true (default).
- The provider keeps one connection and serializes operations on it. Reads download the whole file to a
  temp buffer before returning the stream.
