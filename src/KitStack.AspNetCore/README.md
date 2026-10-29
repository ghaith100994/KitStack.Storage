# KitStack.AspNetCore

ASP.NET Core integration for KitStack.Storage. Since 0.2 this package references no provider SDK; add the
provider packages you use (`KitStack.Storage.Local`, `.S3`, `.Azure`, `.Sftp`, `.Ftp`) and register them on the
builder returned by `AddKitStackStorage` (which now lives in `KitStack.Abstractions`).

## Serving files

```csharp
builder.Services.AddKitStackStorage(builder.Configuration).AddLocal();

app.UseAuthentication();
app.UseAuthorization();
app.MapKitStackStorageFiles("/files", o =>
    o.Authorize = (http, file) => ValueTask.FromResult(http.User.IsInRole("Archive")));
```

- `GET /files/{location}` and, with an `IFileEntryStore`, `GET /files/{id}`.
- Requires an authenticated user by default. Chain `.RequireAuthorization("policy")` or `.AllowAnonymous()`.
- Works for every provider, decrypts encrypted files, supports range requests and sends ETag/Last-Modified.
- Images, audio, video, PDF and plain text are served inline. Everything else (HTML, SVG, scripts, ...) is sent as
  an attachment, with `X-Content-Type-Options: nosniff` and a sandboxing CSP.
- With an `IFileEntryStore` registered, only recorded, non-deleted files are served.

Inside your own controllers:

```csharp
await using var stream = await storage.OpenReadAsync(entry);
return File(stream, entry.ContentType);
```

## Anonymous static files (0.1 behaviour, opt-in)

`app.UseKitStackStorage(configuration)` only serves the Local directory as anonymous static files when
`Storage:Local:ServeAnonymously` is `true`.
