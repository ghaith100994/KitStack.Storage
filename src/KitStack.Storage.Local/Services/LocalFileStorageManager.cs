using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Local.Options;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KitStack.Storage.Local.Services;

/// <summary>
/// Local file system implementation of IFileStorageManager.
/// Uses LocalOptions.Path as base directory.
/// </summary>
public class LocalFileStorageManager : IFileStorageManager
{
    private readonly LocalOptions _options;
    private readonly string _basePath;

    public LocalFileStorageManager(IOptions<LocalOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _basePath = GetBasePath(_options.Path);

        if (_options.EnsureBasePathExists && !Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }
    }

    private static string GetBasePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;

        return Path.Combine(Directory.GetCurrentDirectory(), configuredPath);
    }

    private string GetFullPath(string fileLocation)
    {
        // Normalize and combine safely to avoid directory traversal attacks.
        var combined = Path.Combine(_basePath, fileLocation.TrimStart('/', '\\'));
        var full = Path.GetFullPath(combined);
        if (!full.StartsWith(Path.GetFullPath(_basePath), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invalid file location.");

        return full;
    }

    public async Task CreateAsync(IFileEntry fileEntry, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileEntry);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = GetFullPath(fileEntry.FileLocation);
        var folder = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        // Use FileStream with async copy
        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(fs, cancellationToken);
    }

    public async Task<byte[]> ReadAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(fileEntry.FileLocation);
        return await File.ReadAllBytesAsync(fullPath, cancellationToken);
    }

    public Task<Stream> ReadAsStreamAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(fileEntry.FileLocation);
        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(fileEntry.FileLocation);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task ArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        // Local provider: optionally move to an archive folder under base path
        var source = GetFullPath(fileEntry.FileLocation);
        var archiveFolder = Path.Combine(_basePath, "archive");
        if (!Directory.Exists(archiveFolder))
            Directory.CreateDirectory(archiveFolder);

        var dest = Path.Combine(archiveFolder, Path.GetFileName(source));
        File.Move(source, dest, overwrite: true);
        return Task.CompletedTask;
    }

    public Task UnArchiveAsync(IFileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        var archiveFolder = Path.Combine(_basePath, "archive");
        var destFile = Path.Combine(_basePath, fileEntry.FileLocation.TrimStart('/', '\\'));
        var sourceFile = Path.Combine(archiveFolder, Path.GetFileName(destFile));
        if (File.Exists(sourceFile))
        {
            var folder = Path.GetDirectoryName(destFile);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            File.Move(sourceFile, destFile, overwrite: true);
        }
        return Task.CompletedTask;
    }
}