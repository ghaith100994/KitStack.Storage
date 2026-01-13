namespace KitStack.Storage.S3.Services;

public interface IS3PresignedUrlGenerator
{
    Task<Uri> GeneratePreSignedUploadUrlAsync(string key, TimeSpan expires, string? contentType = null);
    Task<Uri> GeneratePreSignedDownloadUrlAsync(string key, TimeSpan expires);
}
