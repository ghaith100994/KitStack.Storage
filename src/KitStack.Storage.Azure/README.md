# KitStack.Storage.Azure

Azure Blob Storage provider for KitStack.Storage.

```json
"Storage": {
  "Azure": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net",
    "ContainerName": "files",
    "Prefix": "app/",
    "CreateContainerIfNotExists": true,
    "AccessTier": "Hot",
    "VariantAccessTier": "Cool"
  }
}
```

```csharp
builder.Services.AddKitStackStorage(builder.Configuration).AddAzureBlob();
```

Authentication, in order of precedence:
1. A `BlobServiceClient` registered in DI, for example with managed identity:
   `services.AddSingleton(new BlobServiceClient(new Uri("https://acct.blob.core.windows.net"), new DefaultAzureCredential()));`
2. `ConnectionString`.
3. `ServiceUri` (may carry a SAS token).

`GetUrlAsync` returns a read-only SAS URL: signed with the account key when the client has one, otherwise
with a user delegation key (token credentials; max 7 days). It returns `null` for encrypted files and for
clients that cannot sign.

Tiering: `AccessTier` / `VariantAccessTier` set the tier on upload, and
`AzureBlobFileStorageManager.SetAccessTierAsync(entry, AccessTier.Cool)` moves existing files.
Archived blobs must be rehydrated before they can be read.
