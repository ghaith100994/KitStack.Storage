using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using KitStack.Abstractions.Exceptions;
using KitStack.Storage.S3.Helpers;
using KitStack.Storage.S3.Options;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.S3.Services;

public sealed class S3PresignedUrlGenerator : IS3PresignedUrlGenerator, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly S3Options _options;
    private bool _disposed;

    public S3PresignedUrlGenerator(IOptions<S3Options> options)
    {
        _options = options.Value;
        if (_options.MainTarget is null)
        {
            throw new StorageConfigurationException("MainTarget must be configured.");
        }
        _client = BuildClient(_options.MainTarget);
    }

    public Task<Uri> GeneratePreSignedUploadUrlAsync(string key, TimeSpan expires, string? contentType = null)
        => GeneratePreSignedUploadUrlAsync(key, expires, contentType, target: null);

    public Task<Uri> GeneratePreSignedDownloadUrlAsync(string key, TimeSpan expires)
        => GeneratePreSignedDownloadUrlAsync(key, expires, target: null);

    // Overloads that accept an optional target (bucket/region/etc). If target is null the main configured target is used.
    public async Task<Uri> GeneratePreSignedUploadUrlAsync(string key, TimeSpan expires, string? contentType, S3TargetOptions? target)
    {
        var t = target ?? _options.MainTarget;
        var bucket = t.BucketName ?? string.Empty;
        var finalKey = NormalizeKeyWithPrefix(t.Prefix, key);

        var client = CreateClientForTarget(t, out var dispose);
        using var owned = dispose ? client : null;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = finalKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expires)
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            request.ContentType = contentType;

        var url = await client.GetPreSignedURLAsync(request);
        return new Uri(url);
        
    }

    public async Task<Uri> GeneratePreSignedDownloadUrlAsync(string key, TimeSpan expires, S3TargetOptions? target)
    {
        var t = (target ?? _options.MainTarget) ?? throw new StorageConfigurationException("No S3 target configured.");
        var bucket = t.BucketName ?? string.Empty;
        var finalKey = NormalizeKeyWithPrefix(t.Prefix, key);

        var client = CreateClientForTarget(t, out var dispose);
        using var owned = dispose ? client : null;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = finalKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expires)
        };

        var url = await client.GetPreSignedURLAsync(request);
        return new Uri(url);
        
    }

    private static string NormalizeKeyWithPrefix(string? prefix, string key)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return key.TrimStart('/');
        return S3KeyHelper.NormalizeKey(prefix, string.Empty, key);
    }

    private AmazonS3Client CreateClientForTarget(S3TargetOptions target, out bool dispose)
    {
        // If target matches main and main client was provided, reuse it
        var main = _options.MainTarget;
        if (main != null && TargetsMatch(main, target))
        {
            dispose = false;
            return _client;
        }

        dispose = true;
        return BuildClient(target);
    }

    // Per-target credentials first, then the global options, else the SDK default chain.
    private AmazonS3Client BuildClient(S3TargetOptions target)
    {
        var cfg = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(target.ServiceUrl))
        {
            cfg.ServiceURL = target.ServiceUrl;
            cfg.UseHttp = target.ServiceUrl.StartsWith("http:", StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(target.Region))
            cfg.RegionEndpoint = RegionEndpoint.GetBySystemName(target.Region);

        var access = target.AccessKeyID ?? _options.AccessKeyID;
        var secret = target.SecretAccessKey ?? _options.SecretAccessKey;
        return !string.IsNullOrWhiteSpace(access) && !string.IsNullOrWhiteSpace(secret)
            ? new AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials(access, secret), cfg)
            : new AmazonS3Client(cfg);
    }

    private static bool TargetsMatch(S3TargetOptions a, S3TargetOptions b)
    {
        if (a == null || b == null) return false;
        return string.Equals(a.BucketName, b.BucketName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Region, b.Region, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.ServiceUrl, b.ServiceUrl, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Prefix, b.Prefix, StringComparison.OrdinalIgnoreCase);
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
}
