using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace KitStack.AspNetCore.Extensions;

/// <summary>Options for <see cref="FileEndpointRouteBuilderExtensions.MapKitStackStorageFiles"/>.</summary>
public sealed class KitStackFileEndpointOptions
{
    /// <summary>
    /// Per-file authorization (for example "only the owner may download"), evaluated after the endpoint's
    /// authorization policy. Return <c>false</c> to answer 403. Default: every authorised user may read every file.
    /// </summary>
    public Func<HttpContext, IFileEntry, ValueTask<bool>>? Authorize { get; set; }

    /// <summary>Cache-Control for successful responses. Default <c>private, max-age=300</c>.</summary>
    public string CacheControl { get; set; } = "private, max-age=300";
}

/// <summary>
/// Maps an endpoint that streams stored files through <see cref="IFileStorageManager.OpenReadAsync"/>, so files
/// are served with authorization, transparent decryption and the right content type, for any provider.
/// </summary>
public static class FileEndpointRouteBuilderExtensions
{
    // Content types that are safe to render inline; everything else is sent as an attachment so that
    // uploaded HTML/SVG/scripts cannot run in the application's origin.
    private static readonly string[] InlinePrefixes = ["image/", "audio/", "video/"];
    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase) { "application/pdf", "text/plain" };

    /// <summary>
    /// Map <c>GET {pattern}/{**location}</c> and, when an <see cref="IFileEntryStore"/> is registered,
    /// <c>GET {pattern}/{id:guid}</c>. The group requires an authenticated user by default; chain
    /// <c>.RequireAuthorization("policy")</c> to use a policy, or <c>.AllowAnonymous()</c> to opt out.
    /// </summary>
    /// <remarks>
    /// With an <see cref="IFileEntryStore"/>, only files recorded in it (and not deleted) are served.
    /// Without one, any location under the provider is served to authorised users.
    /// </remarks>
    public static RouteGroupBuilder MapKitStackStorageFiles(
        this IEndpointRouteBuilder endpoints, string pattern = "/files", Action<KitStackFileEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new KitStackFileEndpointOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(pattern);
        group.RequireAuthorization();

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http) =>
        {
            var store = http.RequestServices.GetService<IFileEntryStore>();
            if (store is null)
                return Results.NotFound();
            var entry = await store.FindAsync(id, http.RequestAborted).ConfigureAwait(false);
            return entry is null || entry.IsDeleted
                ? Results.NotFound()
                : await ServeAsync(http, entry, options).ConfigureAwait(false);
        }).WithName("KitStackStorageFileById");

        group.MapGet("/{**location}", async (string location, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(location))
                return Results.NotFound();

            IFileEntry? entry;
            var store = http.RequestServices.GetService<IFileEntryStore>();
            if (store is not null)
            {
                entry = await store.FindByLocationAsync(location, http.RequestAborted).ConfigureAwait(false);
                if (entry is null)
                    return Results.NotFound();
            }
            else
            {
                var fileName = location[(location.LastIndexOf('/') + 1)..];
                entry = new FileEntry
                {
                    FileLocation = location,
                    FileName = fileName,
                    ContentType = ContentTypeResolver.FromExtension(Path.GetExtension(fileName)),
                    VariantType = null,
                };
            }

            return await ServeAsync(http, entry, options).ConfigureAwait(false);
        }).WithName("KitStackStorageFileByLocation");

        return group;
    }

    private static async Task<IResult> ServeAsync(HttpContext http, IFileEntry entry, KitStackFileEndpointOptions options)
    {
        if (options.Authorize is not null && !await options.Authorize(http, entry).ConfigureAwait(false))
            return Results.Forbid();

        var storage = http.RequestServices.GetRequiredService<IFileStorageManager>();
        Stream stream;
        try
        {
            stream = await storage.OpenReadAsync(entry, http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageNotFoundException or StorageValidationException)
        {
            // Validation failures (for example path traversal) are reported as 404 so they reveal nothing.
            return Results.NotFound();
        }

        var contentType = string.IsNullOrWhiteSpace(entry.ContentType) ? ContentTypes.OctetStream : entry.ContentType;
        var inline = InlineTypes.Contains(contentType)
                     || (InlinePrefixes.Any(p => contentType.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                         && !contentType.Contains("svg", StringComparison.OrdinalIgnoreCase));

        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        headers.CacheControl = options.CacheControl;

        var etag = string.IsNullOrWhiteSpace(entry.Checksum) ? null : new EntityTagHeaderValue($"\"{entry.Checksum}\"");
        return Results.File(
            stream,
            contentType,
            fileDownloadName: inline ? null : (string.IsNullOrWhiteSpace(entry.FileName) ? "file" : entry.FileName),
            lastModified: entry.UploadedTime == default ? null : entry.UploadedTime,
            entityTag: etag,
            enableRangeProcessing: stream.CanSeek);
    }
}
