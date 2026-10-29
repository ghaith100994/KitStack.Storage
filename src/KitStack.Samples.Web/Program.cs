using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.AspNetCore.Extensions;
using KitStack.Storage.Local.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Shared options (upload policy, encryption) from "Storage", provider from "Storage:Local".
builder.Services.AddKitStackStorage(builder.Configuration).AddLocal();
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthorization();

// Downloads go through an endpoint that requires authorization by default.
// The sample has no login, so it opens the endpoint in Development only.
var files = app.MapKitStackStorageFiles("/files");
if (app.Environment.IsDevelopment())
    files.AllowAnonymous();

app.MapGet("/", () => "POST a file to /upload, then GET the returned url.");

app.MapPost("/upload", async (IFormFile file, IFileStorageManager storage, CancellationToken ct) =>
{
    try
    {
        var entry = await storage.CreateAsync<UploadedDocument>(file, "Samples", ct);
        return Results.Ok(new { entry.Id, entry.FileName, entry.ContentType, entry.Size, entry.Checksum, url = $"/files/{entry.FileLocation}" });
    }
    catch (StorageValidationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).DisableAntiforgery();

app.Run();

internal sealed class UploadedDocument;
