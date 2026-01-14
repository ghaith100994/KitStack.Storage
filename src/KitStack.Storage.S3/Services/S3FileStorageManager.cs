using System.Net.Sockets;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.S3.Util;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Utilities;
using KitStack.Storage.S3.Helpers;
using KitStack.Storage.S3.Options;
using Microsoft.AspNetCore.Http;
using Amazon.Runtime;

namespace KitStack.Storage.S3.Services;

public sealed class S3FileStorageManager : IFileStorageManager, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly S3Options _options;
    private readonly S3TargetOptions _mainTarget;
    private bool _disposed;

    public S3FileStorageManager(S3Options options)
    {
        _options = options;
        _mainTarget = options.MainTarget;

        // Create main client using credentials from target or options or default credential chain
        _client = CreateClientForTarget(_mainTarget);
    }

    public async Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(category))
            throw new StorageValidationException("Category required");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var entityName = typeof(T).Name;
        var typeFolder = ImageProcessingHelper.GetFileTypeFolder(extension);
        var relative = Path.Combine(category, entityName, typeFolder).Replace('\\', '/');
        var key = S3KeyHelper.NormalizeKey(_mainTarget.Prefix ?? string.Empty, relative, $"{Guid.NewGuid():N}-{Path.GetFileName(file.FileName)}{extension}");

        var entry = new FileEntry
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(file.FileName),
            OriginalFileName = file.FileName,
            Size = file.Length,
            ContentType = file.ContentType,
            UploadedTime = DateTime.UtcNow,
            FileExtension = extension,
            Category = category,
            Encrypted = false,
            VariantType = "original",
            StorageProvider = "S3",
            LastAccessedTime = DateTimeOffset.UtcNow,
        };

        using var ms = new MemoryStream();
        await file.OpenReadStream().CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        ms.Seek(0, SeekOrigin.Begin);

        var (client, bucket, dispose) = GetClientAndBucket(_mainTarget);
        try
        {
            await UploadStreamAsync(client, bucket, key, ms, file.ContentType ?? "application/octet-stream", _mainTarget, cancellationToken).ConfigureAwait(false);
            entry.FileLocation = key;
        }
        finally
        {
            if (dispose) client.Dispose();
        }
        return entry;
    }

    public async Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class, IFileAttachable
    {
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);
        entity.AddFileAttachment(primary);
        primary.LinkToEntity(entity, category ?? typeof(T).Name);
        return primary;
    }

    public async Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default) where T : class
    {
        var primary = await CreateAsync<T>(file, category, cancellationToken).ConfigureAwait(false);
        var variants = new List<IFileEntry>();

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!ImageProcessingHelper.IsImageExtension(extension) || _options.ImageProcessing == null)
            return (primary, variants);

        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();

        // compressed
        if (_options.ImageProcessing.CreateCompressed)
        {
            using var src = new MemoryStream(bytes);
            await using var outStream = new MemoryStream();
            await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream,
                _options.ImageProcessing.CompressedMaxWidth,
                _options.ImageProcessing.CompressedMaxHeight,
                _options.ImageProcessing.JpegQuality, cancellationToken).ConfigureAwait(false);
            outStream.Seek(0, SeekOrigin.Begin);

            var compressedTarget = _options.ImageProcessing.CompressedTarget ?? _mainTarget;
            var keyRel = Path.Combine(category ?? string.Empty, typeof(T).Name, ImageProcessingHelper.GetFileTypeFolder(extension), "compressed").Replace('\\', '/');
            var key = S3KeyHelper.NormalizeKey(compressedTarget.Prefix ?? string.Empty, keyRel, $"{Guid.NewGuid():N}-{Path.GetFileName(file.FileName)}{extension}");

            var (clientC, bucketC, disposeC) = GetClientAndBucket(compressedTarget);
            try
            {
                await UploadStreamAsync(clientC, bucketC, key, outStream, "image/jpeg", compressedTarget, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (disposeC) clientC.Dispose();
            }

            FileEntry item = new()
            {
                Id = Guid.NewGuid(),
                FileName = Path.GetFileName(keyRel),
                FileLocation = key,
                Size = outStream.Length,
                ContentType = "image/jpeg",
                UploadedTime = DateTime.UtcNow,
                VariantType = "compressed",
                Category = category,
                Encrypted = false,
                FileExtension = extension,
                OriginalFileName = Path.GetFileName(file.FileName),
                StorageProvider = "S3",
                LastAccessedTime = DateTimeOffset.UtcNow,
            };
            item.CopyRelationsFrom(primary);
            variants.Add(item);
        }

        // thumbnail
        if (_options.ImageProcessing.CreateThumbnail)
        {
            using var src = new MemoryStream(bytes);
            await using var outStream = new MemoryStream();
            await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream,
                _options.ImageProcessing.ThumbnailMaxWidth,
                _options.ImageProcessing.ThumbnailMaxHeight,
                _options.ImageProcessing.JpegQuality, cancellationToken).ConfigureAwait(false);
            outStream.Seek(0, SeekOrigin.Begin);

            var thumbTarget = _options.ImageProcessing.ThumbnailTarget ?? _mainTarget;
            var keyRel = Path.Combine(category ?? string.Empty, typeof(T).Name, ImageProcessingHelper.GetFileTypeFolder(extension), "thumbnails").Replace('\\', '/');
            var key = S3KeyHelper.NormalizeKey(thumbTarget.Prefix ?? string.Empty, keyRel, $"{Guid.NewGuid():N}-{Path.GetFileName(file.FileName)}{extension}");

            var (clientT, bucketT, disposeT) = GetClientAndBucket(thumbTarget);
            try
            {
                await UploadStreamAsync(clientT, bucketT, key, outStream, "image/jpeg", thumbTarget, cancellationToken).ConfigureAwait(false);

            }
            finally
            {
                if (disposeT) clientT.Dispose();
            }

            FileEntry item = new()
            {
                Id = Guid.NewGuid(),
                FileName = Path.GetFileName(keyRel),
                FileLocation = key,
                Size = outStream.Length,
                ContentType = "image/jpeg",
                UploadedTime = DateTime.UtcNow,
                VariantType = "thumbnail",
                Category = category,
                FileExtension = extension,
                Encrypted = false,
                OriginalFileName = Path.GetFileName(file.FileName),
                StorageProvider = "S3",
                LastAccessedTime = DateTimeOffset.UtcNow,
            };
            item.CopyRelationsFrom(primary);
            variants.Add(item);
        }

        // additional sizes
        if (_options.ImageProcessing.AdditionalSizes?.Count > 0)
        {
            foreach (var size in _options.ImageProcessing.AdditionalSizes)
            {
                using var src = new MemoryStream(bytes);
                await using var outStream = new MemoryStream();
                await ImageProcessingHelper.CreateResizedJpegToStreamAsync(src, outStream, size.MaxWidth, size.MaxHeight, size.JpegQuality, cancellationToken).ConfigureAwait(false);
                outStream.Seek(0, SeekOrigin.Begin);

                var target = size.Target ?? _mainTarget;
                var keyRel = Path.Combine(category ?? string.Empty, typeof(T).Name, ImageProcessingHelper.GetFileTypeFolder(extension), size.SizeName).Replace('\\', '/');
                var key = S3KeyHelper.NormalizeKey(target.Prefix ?? string.Empty, keyRel, $"{Guid.NewGuid():N}-{Path.GetFileName(file.FileName)}{extension}");

                var (clientSize, bucketSize, disposeSize) = GetClientAndBucket(target);
                try
                {
                    await UploadStreamAsync(clientSize, bucketSize, key, outStream, "image/jpeg", target, cancellationToken).ConfigureAwait(false);

                }
                finally
                {
                    if (disposeSize) clientSize.Dispose();
                }

                FileEntry item = new()
                {
                    Id = Guid.NewGuid(),
                    FileName = Path.GetFileName(keyRel),
                    FileLocation = key,
                    Size = outStream.Length,
                    ContentType = "image/jpeg",
                    UploadedTime = DateTime.UtcNow,
                    VariantType = size.SizeName,
                    Category = category,
                    FileExtension = extension,
                    Encrypted = false,
                    OriginalFileName = Path.GetFileName(file.FileName),
                    StorageProvider = "S3",
                    LastAccessedTime = DateTimeOffset.UtcNow,
                };
                item.CopyRelationsFrom(primary);
                variants.Add(item);
            }
        }

        return (primary, variants);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _client?.Dispose();
            }
            _disposed = true;
        }
    }

    #region Helpers
    private static async Task UploadStreamAsync(AmazonS3Client client, string bucketName, string key, Stream data, string contentType, S3TargetOptions target, CancellationToken cancellationToken)
    {
        // Ensure stream at beginning
        if (data.CanSeek) data.Seek(0, SeekOrigin.Begin);

        // Apply target/global settings
        var useSse = target?.UseServerSideEncryption == true;
        var kmsKey = target?.KmsKeyId;
        var storageClassStr = target?.StorageClass;
        var cannedAcl = target?.CannedAcl;
        
        var put = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = data,
            ContentType = contentType
        };

        if (useSse && !string.IsNullOrWhiteSpace(kmsKey))
        {
            put.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
            put.ServerSideEncryptionKeyManagementServiceKeyId = kmsKey;
        }

        if (!string.IsNullOrWhiteSpace(storageClassStr))
        {
            put.StorageClass = S3StorageClass.FindValue(storageClassStr);
        }

        if (!string.IsNullOrWhiteSpace(cannedAcl))
        {
            put.CannedACL = S3CannedACL.FindValue(cannedAcl);
        }

        await client.PutObjectAsync(put, cancellationToken).ConfigureAwait(false);
    }

    private static bool TargetsMatch(S3TargetOptions a, S3TargetOptions b)
    {
        if (a == null || b == null) return false;
        return string.Equals(a.BucketName, b.BucketName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Region, b.Region, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.ServiceUrl, b.ServiceUrl, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Prefix, b.Prefix, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.AccessKeyID, b.AccessKeyID, StringComparison.Ordinal)
               && string.Equals(a.SecretAccessKey, b.SecretAccessKey, StringComparison.Ordinal);
    }

    private static AmazonS3Config BuildConfigFromTarget(S3TargetOptions target)
    {
        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(target.ServiceUrl))
        {
            config.ServiceURL = target.ServiceUrl;
            config.UseHttp = target.ServiceUrl.StartsWith("http:", StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(target.Region))
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(target.Region);
        return config;
    }

    private AmazonS3Client CreateClientForTarget(S3TargetOptions target)
    {
        var cfg = BuildConfigFromTarget(target);

        // Prefer explicit per-target credentials, otherwise fall back to global options
        var access = target?.AccessKeyID;
        var secret = target?.SecretAccessKey;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret))
        {
            access = _options.AccessKeyID;
            secret = _options.SecretAccessKey;
        }

        if (!string.IsNullOrWhiteSpace(access) && !string.IsNullOrWhiteSpace(secret))
        {
            var creds = new BasicAWSCredentials(access, secret);
            return new AmazonS3Client(creds, cfg);
        }

        // Use SDK default credential chain
        return new AmazonS3Client(cfg);
    }

    private (AmazonS3Client client, string bucketName, bool dispose) GetClientAndBucket(S3TargetOptions? target)
    {
        var t = target ?? _mainTarget;
        var bucket = t.BucketName ?? _mainTarget.BucketName;
        if (TargetsMatch(t, _mainTarget))
        {
            return (_client, bucket!, false);
        }

        var client = CreateClientForTarget(t);
        return (client, bucket!, true);
    }

    // Public resolver for external helpers to obtain a client and bucket for a given target.
    // Returns IAmazonS3 to avoid exposing concrete SDK client type.
    public (IAmazonS3 client, string bucketName, bool dispose) ResolveClientAndBucket(S3TargetOptions? target)
    {
        var (client, bucket, dispose) = GetClientAndBucket(target);
        return (client, bucket, dispose);
    }
    #endregion
}
