using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using KitStack.Storage.S3.Options;

namespace KitStack.Storage.S3.Helpers;

public static class S3BucketHelper
{
    /// <summary>
    /// Ensure configured buckets exist for main target and any variant targets (best-effort).
    /// The caller provides a function that returns a client, the resolved bucket name and whether the client should be disposed.
    /// </summary>
    public static async Task EnsureBucketsExistAsync(S3Options options, Func<S3TargetOptions?, (IAmazonS3 client, string bucketName, bool dispose)> getClientAndBucket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getClientAndBucket);

        var main = options.MainTarget;
        var targets = new List<S3TargetOptions?> { main };

        if (options.ImageProcessing != null)
        {
            if (options.ImageProcessing.CompressedTarget != null)
                targets.Add(options.ImageProcessing.CompressedTarget);
            if (options.ImageProcessing.ThumbnailTarget != null)
                targets.Add(options.ImageProcessing.ThumbnailTarget);
            if (options.ImageProcessing.AdditionalSizes != null)
            {
                foreach (var s in options.ImageProcessing.AdditionalSizes)
                    if (s?.Target != null)
                        targets.Add(s.Target);
            }
        }

        var checkedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (target == null) continue;
            var bucket = target.BucketName;
            if (string.IsNullOrWhiteSpace(bucket)) continue;
            if (!target.EnsureBucketExists) continue;

            var key = $"{bucket}|{target.Region}|{target.ServiceUrl}";
            if (!checkedKeys.Add(key)) continue;

            var (client, bucketName, dispose) = getClientAndBucket(target);
            try
            {
                var exists = await AmazonS3Util.DoesS3BucketExistV2Async(client, bucketName).ConfigureAwait(false);
                if (!exists)
                {
                    await client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // best-effort
            }
            finally
            {
                if (dispose) client.Dispose();
            }
        }
    }
}
