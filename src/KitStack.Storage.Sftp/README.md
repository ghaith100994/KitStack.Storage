# KitStack.Storage.Sftp

SFTP/FTP storage provider for KitStack.Storage. Provides an implementation of `IFileStorageManager` that uploads files to SFTP/FTP servers using `Renci.SshNet`.

Configuration section: `Storage:Sftp` or `Sftp` depending on registration overload. Provide host, port, username and either password or private key.

This provider includes:
- Options binding classes
- `SftpFileStorageManager` implementing `IFileStorageManager` (basic upload to configured path)
- `SftpHealthCheck` to verify connectivity and write/delete a small test file
- `SftpServiceCollectionExtensions` for DI registration

Note: This is a minimal integration sample. Production use should add robust error handling, pooling, and secure credential management.