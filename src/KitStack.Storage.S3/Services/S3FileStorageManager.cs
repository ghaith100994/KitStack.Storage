using System.Collections.Concurrent;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using KitStack.Abstractions.Services;
using KitStack.Storage.S3.Helpers;
using KitStack.Storage.S3.Options;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.S3.Services;

/// <summary>
/// Amazon S3 (and S3-compatible) implementation of <c>IFileStorageManager</c>.
/// Image variants can be routed to their own bucket via <see cref="S3ImageProcessingOptions"/>.
/// </summary>
public sealed class S3FileStorageManager : FileStorageManagerBase, IDisposable
{
    private readonly S3Options _options;
    private readonly S3TargetOptions _mainTarget;
    private readonly ConcurrentDictionary<string, AmazonS3Client> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    protected override string StorageProvider => "S3";

    public S3FileStorageManager(
        IOptions<S3Options> options,
        IOptions<StorageOptions>? storageOptions = null,
        IFileContentEncryptor? encryptor = null)
        : base(storageOptions?.Value, encryptor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _mainTarget = _options.MainTarget;
    }

    protected override ImageProcessingOptions? GetImageProcessingOptions() => _options.ImageProcessing;

    protected override async Task<string> WriteCoreAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = ResolveTarget(request.VariantType);
        var key = S3KeyHelper.NormalizeKey(target.Prefix ?? string.Empty, request.Folder, request.FileName);
        var (client, bucket) = GetClientAndBucket(target);
        await UploadStreamAsync(client, bucket, key, request.Content, request.ContentType, target, cancellationToken).ConfigureAwait(false);
        return key;
    }

    protected override async Task<Stream?> OpenReadCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        var (client, bucket) = GetClientAndBucket(ResolveTarget(location.VariantType));
        try
        {
            var response = await client.GetObjectAsync(bucket, location.Location, cancellationToken).ConfigureAwait(false);
            return new ResponseOwningStream(response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    protected override async Task<bool> ExistsCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        var (client, bucket) = GetClientAndBucket(ResolveTarget(location.VariantType));
        try
        {
            await client.GetObjectMetadataAsync(bucket, location.Location, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    protected override async Task DeleteCoreAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        // DeleteObject succeeds for missing keys.
        var (client, bucket) = GetClientAndBucket(ResolveTarget(location.VariantType));
        await client.DeleteObjectAsync(bucket, location.Location, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<Uri?> GetUrlCoreAsync(StorageLocation location, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        var (client, bucket) = GetClientAndBucket(ResolveTarget(location.VariantType));
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = location.Location,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromSeconds(_options.PresignedUrlExpirationSeconds)),
        }).ConfigureAwait(false);
        return new Uri(url);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
        _disposed = true;
    }

    #region Helpers
    private S3TargetOptions ResolveTarget(string variantType)
    {
        var ip = _options.ImageProcessing;
        return variantType switch
        {
            OriginalVariant => _mainTarget,
            "compressed" => ip?.CompressedTarget ?? _mainTarget,
            "thumbnail" => ip?.ThumbnailTarget ?? _mainTarget,
            _ => ip?.AdditionalSizes.FirstOrDefault(s => s.SizeName == variantType) is S3ImageSizeOption { Target: { } sizeTarget }
                ? sizeTarget
                : _mainTarget,
        };
    }

    private static async Task UploadStreamAsync(AmazonS3Client client, string bucketName, string key, Stream data, string contentType, S3TargetOptions target, CancellationToken cancellationToken)
    {
        if (data.CanSeek) data.Seek(0, SeekOrigin.Begin);

        var put = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = data,
            AutoCloseStream = false,
            ContentType = contentType,
        };

        if (target.UseServerSideEncryption == true)
        {
            if (string.IsNullOrWhiteSpace(target.KmsKeyId))
            {
                put.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }
            else
            {
                put.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                put.ServerSideEncryptionKeyManagementServiceKeyId = target.KmsKeyId;
            }
        }

        if (!string.IsNullOrWhiteSpace(target.StorageClass))
            put.StorageClass = S3StorageClass.FindValue(target.StorageClass);

        if (!string.IsNullOrWhiteSpace(target.CannedAcl))
            put.CannedACL = S3CannedACL.FindValue(target.CannedAcl);

        await client.PutObjectAsync(put, cancellationToken).ConfigureAwait(false);
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
        var access = target.AccessKeyID;
        var secret = target.SecretAccessKey;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret))
        {
            access = _options.AccessKeyID;
            secret = _options.SecretAccessKey;
        }

        if (!string.IsNullOrWhiteSpace(access) && !string.IsNullOrWhiteSpace(secret))
            return new AmazonS3Client(new BasicAWSCredentials(access, secret), cfg);

        // Use SDK default credential chain
        return new AmazonS3Client(cfg);
    }

    private (AmazonS3Client client, string bucketName) GetClientAndBucket(S3TargetOptions? target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var t = target ?? _mainTarget;
        var bucket = t.BucketName ?? _mainTarget.BucketName!;
        var cacheKey = string.Join('|', t.Region, t.ServiceUrl, t.AccessKeyID, t.SecretAccessKey);
        return (_clients.GetOrAdd(cacheKey, _ => CreateClientForTarget(t)), bucket);
    }

    /// <summary>
    /// Resolve the client and bucket used for a target, for external helpers (bucket creation, custom operations).
    /// The client is owned by this manager: do not dispose it (<c>dispose</c> is always <c>false</c>).
    /// </summary>
    public (IAmazonS3 client, string bucketName, bool dispose) ResolveClientAndBucket(S3TargetOptions? target)
    {
        var (client, bucket) = GetClientAndBucket(target);
        return (client, bucket, false);
    }

    /// <summary>Ensure the configured buckets exist (targets with <c>EnsureBucketExists = true</c>).</summary>
    public Task EnsureBucketsExistAsync(CancellationToken cancellationToken = default)
        => S3BucketHelper.EnsureBucketsExistAsync(_options, ResolveClientAndBucket, cancellationToken);

    /// <summary>Stream over a GetObject response that disposes the response with it.</summary>
    private sealed class ResponseOwningStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.ContentLength;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    #endregion
}
