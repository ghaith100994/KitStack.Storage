using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using KitStack.Storage.Local.Options;
using Microsoft.AspNetCore.StaticFiles;
using KitStack.AspNetCore.Extensions;

namespace KitStack.AspNetCore.Middleware;

/// <summary>
/// Middleware that serves static files from a runtime-configurable local path and URL prefix.
/// It listens to IOptionsMonitor<LocalOptions> changes and updates the underlying file provider root.
/// </summary>
public class DynamicStaticFileMiddleware : IMiddleware, IDisposable
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<LocalOptions> _optionsMonitor;
    private readonly DynamicPhysicalFileProvider _provider;
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new FileExtensionContentTypeProvider();
    private bool _disposed;

    public DynamicStaticFileMiddleware(RequestDelegate next, IOptionsMonitor<LocalOptions> optionsMonitor)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));

        var opts = _optionsMonitor.CurrentValue ?? new LocalOptions();
        var basePath = Path.IsPathRooted(opts.Path) ? opts.Path : Path.Combine(Directory.GetCurrentDirectory(), opts.Path);
        if (!Directory.Exists(basePath) && opts.EnsureBasePathExists)
            Directory.CreateDirectory(basePath);

        _provider = new DynamicPhysicalFileProvider(basePath);

        _optionsMonitor.OnChange(newOpts =>
        {
            try
            {
                var newBase = Path.IsPathRooted(newOpts.Path) ? newOpts.Path : Path.Combine(Directory.GetCurrentDirectory(), newOpts.Path);
                if (!Directory.Exists(newBase) && newOpts.EnsureBasePathExists)
                    Directory.CreateDirectory(newBase);
                _provider.UpdateRoot(newBase);
            }
            catch
            {
                // best-effort
            }
        });
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        await InvokeInternalAsync(context, next).ConfigureAwait(false);
    }

    // Retain the original logic for internal use
    private async Task InvokeInternalAsync(HttpContext context, RequestDelegate next)
    {
        var opts = _optionsMonitor.CurrentValue ?? new LocalOptions();
        var requestPath = "/" + opts.Path.Trim('/');

        var requestPathString = new PathString(requestPath);

        if (!context.Request.Path.StartsWithSegments(requestPathString, out var matched, out var remaining))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var subpath = remaining.HasValue ? remaining.Value : "/";
        // Trim leading slash for file provider
        var fileRelative = subpath.TrimStart('/');

        if (string.IsNullOrEmpty(fileRelative))
        {
            // try index.html
            fileRelative = "index.html";
        }

        var fileInfo = _provider.GetFileInfo(fileRelative);
        if (fileInfo == null || !fileInfo.Exists || fileInfo.IsDirectory)
        {
            // not found, let next middleware handle
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!_contentTypeProvider.TryGetContentType(fileInfo.Name, out var contentType))
            contentType = "application/octet-stream";

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;

        await using var stream = fileInfo.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
    }

    // For compatibility with existing usages
    public async Task InvokeAsync(HttpContext context)
    {
        await InvokeInternalAsync(context, _next).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _provider?.Dispose();
            }
            _disposed = true;
        }
    }
}
