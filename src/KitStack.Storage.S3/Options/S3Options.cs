
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using KitStack.Abstractions.Options;

namespace KitStack.Storage.S3.Options;

public class S3Options : IValidatableObject
{
    // Optional default credentials used when targets do not provide their own.
    public string? AccessKeyID { get; set; }
    public string? SecretAccessKey { get; set; }

    // Main/default target configuration
    public S3TargetOptions MainTarget { get; set; } = new S3TargetOptions();

    public int PresignedUrlExpirationSeconds { get; set; } = 900; // 15 minutes

    public S3ImageProcessingOptions? ImageProcessing { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        if (MainTarget == null)
        {
            results.Add(new ValidationResult("MainTarget is required.", [nameof(MainTarget)]));
            return results;
        }

        if (string.IsNullOrWhiteSpace(MainTarget.BucketName))
            results.Add(new ValidationResult("MainTarget.BucketName is required.", [$"{nameof(MainTarget)}.{nameof(MainTarget.BucketName)}"]));

        if (!string.IsNullOrWhiteSpace(AccessKeyID) ^ !string.IsNullOrWhiteSpace(SecretAccessKey))
            results.Add(new ValidationResult("Both AccessKeyID and SecretAccessKey must be provided together (or neither).", new[] { nameof(AccessKeyID), nameof(SecretAccessKey) }));

        void CheckTarget(S3TargetOptions? t, string name)
        {
            if (t == null) return;
            if (string.IsNullOrWhiteSpace(t.BucketName))
                results.Add(new ValidationResult($"{name}.BucketName is required.", [$"{name}.BucketName"]));
            if (!string.IsNullOrWhiteSpace(t.AccessKeyID) ^ !string.IsNullOrWhiteSpace(t.SecretAccessKey))
                results.Add(new ValidationResult($"{name} credentials are incomplete (both AccessKeyID and SecretAccessKey required).", new[] { $"{name}.AccessKeyID", $"{name}.SecretAccessKey" }));
        }

        CheckTarget(MainTarget, nameof(MainTarget));

        if (ImageProcessing != null)
        {
            CheckTarget(ImageProcessing.CompressedTarget, $"{nameof(ImageProcessing)}.{nameof(ImageProcessing.CompressedTarget)}");
            CheckTarget(ImageProcessing.ThumbnailTarget, $"{nameof(ImageProcessing)}.{nameof(ImageProcessing.ThumbnailTarget)}");

            if (ImageProcessing.AdditionalSizes != null)
            {
                for (int i = 0; i < ImageProcessing.AdditionalSizes.Count; i++)
                {
                    var s = ImageProcessing.AdditionalSizes[i];
                    if (s == null) continue;
                    if (string.IsNullOrWhiteSpace(s.SizeName))
                        results.Add(new ValidationResult($"ImageProcessing.AdditionalSizes[{i}].SizeName is required.", [$"{nameof(ImageProcessing)}.{nameof(ImageProcessing.AdditionalSizes)}[{i}].{nameof(s.SizeName)}"]));
                    if (s.MaxWidth <= 0 || s.MaxHeight <= 0)
                        results.Add(new ValidationResult($"ImageProcessing.AdditionalSizes[{i}] must have positive MaxWidth/MaxHeight.", [$"{nameof(ImageProcessing)}.{nameof(ImageProcessing.AdditionalSizes)}[{i}]"]));
                    if (s is S3ImageSizeOption s3Size && s3Size.Target != null && string.IsNullOrWhiteSpace(s3Size.Target.BucketName))
                        results.Add(new ValidationResult($"ImageProcessing.AdditionalSizes[{i}].Target.BucketName is required when a Target is provided.", [$"{nameof(ImageProcessing)}.{nameof(ImageProcessing.AdditionalSizes)}[{i}].Target.BucketName"]));
                    if (s is S3ImageSizeOption s3SizeTarget)
                        CheckTarget(s3SizeTarget.Target, $"{nameof(ImageProcessing)}.{nameof(ImageProcessing.AdditionalSizes)}[{i}].Target");
                }
            }
        }

        return results;
    }
}

/// <summary>
/// Extends <see cref="ImageProcessingOptions"/> with S3-specific per-variant target routing.
/// </summary>
public class S3ImageProcessingOptions : ImageProcessingOptions
{
    /// <summary>Optional target for the compressed variant. Falls back to the main target when null.</summary>
    public S3TargetOptions? CompressedTarget { get; set; }

    /// <summary>Optional target for the thumbnail variant. Falls back to the main target when null.</summary>
    public S3TargetOptions? ThumbnailTarget { get; set; }
}

/// <summary>
/// Extends <see cref="ImageSizeOption"/> with an optional S3 target for per-size routing.
/// </summary>
public class S3ImageSizeOption : ImageSizeOption
{
    /// <summary>Optional S3 target for this size. Falls back to the main target when null.</summary>
    public S3TargetOptions? Target { get; set; }
}

/// <summary>
/// Target options for where to upload a variant. If properties are null/empty the main S3Options values are used.
/// </summary>
public class S3TargetOptions
{
    // Optional per-target credentials. If not set, S3Options.AccessKeyID/SecretAccessKey are used.
    public string? AccessKeyID { get; set; }
    public string? SecretAccessKey { get; set; }

    public string? BucketName { get; set; }
    public bool EnsureBucketExists { get; set; }
    public string? Region { get; set; }
    public string? ServiceUrl { get; set; }
    public string? Prefix { get; set; }
    public bool? UseServerSideEncryption { get; set; }
    public string? KmsKeyId { get; set; }
    public string? StorageClass { get; set; }


    /// <summary>
    /// Optional canned ACL to apply to uploaded objects (e.g. "Private", "PublicRead").
    /// If not set, no canned ACL will be applied and bucket defaults are used.
    /// </summary>
    public string? CannedAcl { get; set; }
}
