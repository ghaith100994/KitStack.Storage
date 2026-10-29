using System.ComponentModel.DataAnnotations;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Storage.S3.Extensions;
using KitStack.Storage.S3.HealthChecks;
using KitStack.Storage.S3.Helpers;
using KitStack.Storage.S3.Options;
using KitStack.Storage.S3.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests.S3;

public class S3FileStorageManagerTests
{
    private readonly Dictionary<string, IAmazonS3> _clients = [];
    private readonly List<(IAmazonS3 Client, PutObjectRequest Request, byte[] Body)> _puts = [];
    private readonly Dictionary<(string Bucket, string Key), byte[]> _objects = [];

    private IAmazonS3 ClientFor(S3TargetOptions target)
    {
        var name = target.BucketName ?? "main";
        if (_clients.TryGetValue(name, out var existing))
            return existing;

        var client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<PutObjectRequest>();
            using var ms = new MemoryStream();
            request.InputStream.CopyTo(ms);
            _puts.Add((client, request, ms.ToArray()));
            _objects[(request.BucketName, request.Key)] = ms.ToArray();
            return new PutObjectResponse();
        });
        client.GetObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            _objects.TryGetValue((ci.ArgAt<string>(0), ci.ArgAt<string>(1)), out var body)
                ? Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(body), ContentLength = body.Length })
                : Task.FromException<GetObjectResponse>(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound }));
        client.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            _objects.ContainsKey((ci.ArgAt<string>(0), ci.ArgAt<string>(1)))
                ? Task.FromResult(new GetObjectMetadataResponse())
                : Task.FromException<GetObjectMetadataResponse>(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound }));
        client.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _objects.Remove((ci.ArgAt<string>(0), ci.ArgAt<string>(1)));
            return new DeleteObjectResponse();
        });
        _clients[name] = client;
        return client;
    }

    private static S3Options Options(Action<S3Options>? configure = null)
    {
        var options = new S3Options { MainTarget = new S3TargetOptions { BucketName = "main", Prefix = "/tenant-a/" } };
        configure?.Invoke(options);
        return options;
    }

    private S3FileStorageManager Create(S3Options options, StorageOptions? storage = null)
        => new(MsOptions.Create(options), ClientFor, storage is null ? null : MsOptions.Create(storage));

    [Fact]
    public async Task Uploads_under_the_prefix_and_reads_back()
    {
        var storage = Create(Options());
        var pdf = TestData.Pdf();

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "a.pdf", "text/plain", "Billing");

        var put = Assert.Single(_puts);
        Assert.Equal("main", put.Request.BucketName);
        Assert.StartsWith("tenant-a/Billing/Invoice/Others/", put.Request.Key, StringComparison.Ordinal);
        Assert.Equal("application/pdf", put.Request.ContentType);
        Assert.False(put.Request.AutoCloseStream);
        Assert.Equal(put.Request.Key, entry.FileLocation);
        Assert.Equal("S3", entry.StorageProvider);
        Assert.Null(put.Request.ServerSideEncryptionMethod);

        Assert.True(await storage.ExistsAsync(entry));
        await using var stream = await storage.OpenReadAsync(entry);
        Assert.Equal(pdf.Length, stream.Length);
        Assert.False(stream.CanSeek);
        Assert.Equal(pdf, await TestData.ReadAllAsync(stream));
    }

    [Fact]
    public async Task Response_stream_supports_sync_reads_and_disposes_the_response()
    {
        var storage = Create(Options());
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("sync")), "a.txt", null, "Docs");

        var stream = await storage.OpenReadAsync(entry);
        var buffer = new byte[2];
        Assert.Equal(2, stream.Read(buffer, 0, 2));
        Assert.Equal(2, stream.Read(buffer.AsSpan()));
        Assert.Equal(0, await stream.ReadAsync(buffer, 0, 2));
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        stream.Flush();
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, 1));
    }

    [Fact]
    public async Task Missing_objects_are_not_found()
    {
        var storage = Create(Options());
        var missing = new FileEntry { FileLocation = "nope" };

        Assert.False(await storage.ExistsAsync(missing));
        await Assert.ThrowsAsync<StorageNotFoundException>(() => storage.OpenReadAsync(missing));
    }

    [Fact]
    public async Task Other_S3_errors_propagate()
    {
        var storage = Create(Options());
        ClientFor(new S3TargetOptions { BucketName = "main" }).GetObjectAsync(default!, default!, default).ReturnsForAnyArgs(
            Task.FromException<GetObjectResponse>(new AmazonS3Exception("denied") { StatusCode = HttpStatusCode.Forbidden }));

        await Assert.ThrowsAsync<AmazonS3Exception>(() => storage.OpenReadAsync(new FileEntry { FileLocation = "x" }));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(true, null, "AES256")]
    [InlineData(true, "kms-key", "aws:kms")]
    [InlineData(false, "kms-key", null)]
    public async Task Server_side_encryption_follows_the_target(bool? sse, string? kms, string? expected)
    {
        var storage = Create(Options(o =>
        {
            o.MainTarget.UseServerSideEncryption = sse;
            o.MainTarget.KmsKeyId = kms;
            o.MainTarget.StorageClass = "STANDARD_IA";
            o.MainTarget.CannedAcl = "private";
        }));

        await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");

        var request = Assert.Single(_puts).Request;
        Assert.Equal(expected, request.ServerSideEncryptionMethod?.Value);
        Assert.Equal(expected == "aws:kms" ? kms : null, request.ServerSideEncryptionKeyManagementServiceKeyId);
        Assert.Equal("STANDARD_IA", request.StorageClass.Value);
        Assert.Equal("private", request.CannedACL.Value);
    }

    [Fact]
    public async Task Variants_are_routed_to_their_own_targets_and_deleted_there()
    {
        var images = new S3ImageProcessingOptions { ThumbnailTarget = new S3TargetOptions { BucketName = "thumbs", Prefix = "t" } };
        images.AdditionalSizes.Add(new S3ImageSizeOption { SizeName = "small", MaxWidth = 32, MaxHeight = 32, Target = new S3TargetOptions { BucketName = "small" } });
        images.AdditionalSizes.Add(new ImageSizeOption { SizeName = "medium", MaxWidth = 64, MaxHeight = 64 });
        var storage = Create(Options(o => o.ImageProcessing = images));

        var (primary, variants) = await storage.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");

        Assert.Equal(["main", "main", "thumbs", "small", "main"], _puts.Select(p => p.Request.BucketName));
        Assert.StartsWith("t/Gallery/Invoice/Images/thumbnails/", variants[1].FileLocation, StringComparison.Ordinal);
        Assert.All(_puts.Skip(1), p => Assert.Equal("image/jpeg", p.Request.ContentType));
        // Same endpoint and credentials: one client serves every bucket.
        Assert.Single(_clients);

        await storage.DeleteAsync(primary);

        Assert.Empty(_objects);
        await _clients["main"].Received(1).DeleteObjectAsync("thumbs", variants[1].FileLocation, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Overwrite_replaces_the_object_at_the_same_key()
    {
        var storage = Create(Options());
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("v1")), "a.txt", null, "Docs");

        await storage.OverwriteAsync(entry, new MemoryStream(TestData.Text("v2")));

        Assert.Equal(2, _puts.Count);
        Assert.Equal(entry.FileLocation, _puts[1].Request.Key);
        Assert.Equal(TestData.Text("v2"), _objects[("main", entry.FileLocation)]);
    }

    [Fact]
    public async Task Encrypted_uploads_are_ciphertext_labelled_octet_stream()
    {
        var storage = Create(Options(), new StorageOptions { MasterEncryptionKey = TestData.NewKey() });
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "a.pdf", null, "Docs");

        Assert.Equal("application/octet-stream", _puts[0].Request.ContentType);
        Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
        Assert.Null(await storage.GetUrlAsync(entry));
    }

    [Fact]
    public async Task Presigned_urls_use_the_requested_or_configured_lifetime()
    {
        var options = Options(o => o.PresignedUrlExpirationSeconds = 120);
        var storage = Create(options);
        var client = ClientFor(options.MainTarget);
        client.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://main.s3/x?sig=1");
        var entry = new FileEntry { FileLocation = "tenant-a/x.pdf" };

        Assert.Equal("https://main.s3/x?sig=1", (await storage.GetUrlAsync(entry))!.ToString());
        await storage.GetUrlAsync(entry, TimeSpan.FromMinutes(30));

        var requests = client.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<GetPreSignedUrlRequest>().ToList();
        Assert.All(requests, r => Assert.Equal((HttpVerb.GET, "main", "tenant-a/x.pdf"), (r.Verb, r.BucketName, r.Key)));
        Assert.InRange((requests[0].Expires!.Value - DateTime.UtcNow).TotalSeconds, 100, 121);
        Assert.InRange((requests[1].Expires!.Value - DateTime.UtcNow).TotalMinutes, 29, 30.1);
    }

    [Fact]
    public async Task Real_client_signs_urls_offline_with_explicit_credentials()
    {
        var options = new S3Options
        {
            AccessKeyID = "AKIAEXAMPLE",
            SecretAccessKey = "secret",
            MainTarget = new S3TargetOptions { BucketName = "docs", Region = "eu-west-1" },
        };
        using var storage = new S3FileStorageManager(MsOptions.Create(options));

        var url = await storage.GetUrlAsync(new FileEntry { FileLocation = "a/b.pdf" }, TimeSpan.FromMinutes(10));

        Assert.Contains("docs", url!.Host + url.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("a/b.pdf", url.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=600", url.Query, StringComparison.Ordinal);
        Assert.Contains("AKIAEXAMPLE", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Targets_with_their_own_endpoint_get_their_own_client()
    {
        var images = new S3ImageProcessingOptions { ThumbnailTarget = new S3TargetOptions { BucketName = "thumbs", ServiceUrl = "http://minio:9000" } };
        var storage = Create(Options(o => o.ImageProcessing = images));

        await storage.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");

        Assert.Same(_clients["thumbs"], _puts.Single(p => p.Request.BucketName == "thumbs").Client);
        Assert.Equal(2, _clients.Count);
    }

    [Fact]
    public void Clients_are_cached_per_target_and_disposed_with_the_manager()
    {
        var storage = Create(Options(o => o.ImageProcessing = new S3ImageProcessingOptions { CompressedTarget = new S3TargetOptions { BucketName = "c" } }));

        var (a, bucketA, disposeA) = storage.ResolveClientAndBucket(null);
        var (b, _, _) = storage.ResolveClientAndBucket(new S3TargetOptions { BucketName = "main", Prefix = "/tenant-a/" });
        var (c, bucketC, _) = storage.ResolveClientAndBucket(new S3TargetOptions { BucketName = "c", Region = "us-east-1" });

        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(("main", "c", false), (bucketA, bucketC, disposeA));

        storage.Dispose();
        storage.Dispose();
        a.Received(1).Dispose();
        c.Received(1).Dispose();
        Assert.Throws<ObjectDisposedException>(() => storage.ResolveClientAndBucket(null));
    }
}

public class S3SupportTests
{
    [Theory]
    [InlineData("", "a/b", "f.txt", "a/b/f.txt")]
    [InlineData("/p/", "/a/b/", "f.txt", "p/a/b/f.txt")]
    [InlineData("p", "", "f.txt", "p/f.txt")]
    [InlineData(null, "a\\b", "f.txt", "a/b/f.txt")]
    public void Keys_are_normalised(string? prefix, string relative, string file, string expected)
        => Assert.Equal(expected, S3KeyHelper.NormalizeKey(prefix!, relative, file));

    [Fact]
    public void Options_validation_reports_each_problem()
    {
        var options = new S3Options
        {
            AccessKeyID = "only-the-key",
            MainTarget = new S3TargetOptions(),
            ImageProcessing = new S3ImageProcessingOptions
            {
                ThumbnailTarget = new S3TargetOptions { BucketName = "t", AccessKeyID = "x" },
                CompressedTarget = new S3TargetOptions(),
            },
        };
        options.ImageProcessing.AdditionalSizes.Add(new S3ImageSizeOption { SizeName = "", MaxWidth = 0, MaxHeight = 1, Target = new S3TargetOptions() });

        var messages = Validate(options);

        Assert.Contains("MainTarget.BucketName is required.", messages);
        Assert.Contains(messages, m => m.StartsWith("Both AccessKeyID", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("ThumbnailTarget credentials are incomplete", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("CompressedTarget.BucketName", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("AdditionalSizes[0].SizeName", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("positive MaxWidth", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("AdditionalSizes[0].Target.BucketName", StringComparison.Ordinal));

        Assert.Empty(Validate(new S3Options { MainTarget = new S3TargetOptions { BucketName = "ok" } }));
        Assert.Single(Validate(new S3Options { MainTarget = null! }));
    }

    [Fact]
    public async Task Bucket_helper_checks_each_target_once_and_ignores_failures()
    {
        var images = new S3ImageProcessingOptions
        {
            CompressedTarget = new S3TargetOptions { BucketName = "main", EnsureBucketExists = true },
            ThumbnailTarget = new S3TargetOptions { BucketName = "thumbs", EnsureBucketExists = true },
        };
        images.AdditionalSizes.Add(new S3ImageSizeOption { SizeName = "s", Target = new S3TargetOptions { BucketName = "skip", EnsureBucketExists = false } });
        var options = new S3Options { MainTarget = new S3TargetOptions { BucketName = "main", EnsureBucketExists = true }, ImageProcessing = images };

        var asked = new List<string>();
        var disposable = new List<IAmazonS3>();
        await S3BucketHelper.EnsureBucketsExistAsync(options, target =>
        {
            asked.Add(target!.BucketName!);
            var client = Substitute.For<IAmazonS3>();
            disposable.Add(client);
            return (client, target.BucketName!, true);
        });

        Assert.Equal(["main", "thumbs"], asked);
        Assert.All(disposable, c => c.Received(1).Dispose());
        await Assert.ThrowsAsync<ArgumentNullException>(() => S3BucketHelper.EnsureBucketsExistAsync(null!, _ => default));
    }

    [Fact]
    public async Task Health_check_is_unhealthy_when_the_endpoint_is_unreachable()
    {
        var options = new S3Options
        {
            AccessKeyID = "a",
            SecretAccessKey = "b",
            MainTarget = new S3TargetOptions { BucketName = "x", ServiceUrl = "http://127.0.0.1:1", Region = "us-east-1", Prefix = "hc" },
        };
        using var check = new S3HealthCheck(MsOptions.Create(options));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("s3", check, HealthStatus.Unhealthy, null) }, cts.Token);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Presigned_url_generator_signs_uploads_and_downloads_offline()
    {
        var options = new S3Options
        {
            AccessKeyID = "AKIAEXAMPLE",
            SecretAccessKey = "secret",
            MainTarget = new S3TargetOptions { BucketName = "docs", Region = "eu-west-1", Prefix = "p" },
        };
        using var generator = new S3PresignedUrlGenerator(MsOptions.Create(options));

        var upload = await generator.GeneratePreSignedUploadUrlAsync("a.pdf", TimeSpan.FromMinutes(5), "application/pdf");
        var download = await generator.GeneratePreSignedDownloadUrlAsync("a.pdf", TimeSpan.FromMinutes(5));
        var other = await generator.GeneratePreSignedDownloadUrlAsync("b.pdf", TimeSpan.FromMinutes(1), new S3TargetOptions { BucketName = "other", Region = "us-east-1" });

        Assert.Contains("p/a.pdf", upload.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=300", download.Query, StringComparison.Ordinal);
        Assert.Contains("other", other.Host + other.AbsolutePath, StringComparison.Ordinal);
        Assert.Throws<StorageConfigurationException>(() => new S3PresignedUrlGenerator(MsOptions.Create(new S3Options { MainTarget = null! })));
    }

    [Fact]
    public async Task Registration_validates_and_shares_one_manager()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:S3:MainTarget:BucketName"] = "docs",
            ["Storage:S3:MainTarget:Region"] = "eu-west-1",
            ["Storage:S3:AccessKeyID"] = "a",
            ["Storage:S3:SecretAccessKey"] = "b",
        }).Build();
        var services = new ServiceCollection();
        services.AddKitStackStorage(config).AddS3(o => o.PresignedUrlExpirationSeconds = 60);
        await using var sp = services.BuildServiceProvider();

        Assert.Same(sp.GetRequiredService<S3FileStorageManager>(), sp.GetRequiredService<IFileStorageManager>());
        Assert.Equal(60, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3Options>>().Value.PresignedUrlExpirationSeconds);
        await sp.EnsureS3BucketsExistAsync(); // no target asks for creation: no calls

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            new ServiceCollection().AddS3StorageManager(new S3Options { MainTarget = new S3TargetOptions() }));
        using var direct = new ServiceCollection().AddS3StorageManager(new S3Options { MainTarget = new S3TargetOptions { BucketName = "x", Region = "eu-west-1" } }).BuildServiceProvider();
        Assert.NotNull(direct.GetRequiredService<IS3PresignedUrlGenerator>());
    }

    private static List<string> Validate(S3Options options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage!).ToList();
    }
}
