using KitStack.Storage.Abstractions.DependencyInjection;
using KitStack.Storage.Abstractions.Exceptions;
using KitStack.Storage.Abstractions.Extensions;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using KitStack.Storage.Abstractions.Options;
using KitStack.Storage.Fakes.Contracts;
using KitStack.Storage.Fakes.Extensions;
using KitStack.Storage.Local.Extensions;
using KitStack.Storage.Local.Services;
using KitStack.Storage.S3.Extensions;
using KitStack.Storage.S3.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Tests.Instances;

public sealed class NamedInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kitstack-named-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Two_local_roots_are_isolated_and_unkeyed_resolution_is_default()
    {
        var services = new ServiceCollection();
        services.AddKitStackStorage()
            .AddLocal(o => o.Path = Path.Combine(_root, "default"))
            .AddLocal("public-assets", o => o.Path = Path.Combine(_root, "public"))
            .AddLocal("archive", o => o.Path = Path.Combine(_root, "archive"));
        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        var unkeyed = sp.GetRequiredService<IFileStorageManager>();
        var publicAssets = provider.Get("PUBLIC-ASSETS");
        var archive = sp.GetRequiredKeyedService<IFileStorageManager>("archive");
        Assert.Equal(("default", "public-assets", "archive"), (unkeyed.StorageName, publicAssets.StorageName, archive.StorageName));
        Assert.Same(unkeyed, provider.Get("default").Unwrap<LocalFileStorageManager>());
        Assert.Equal(new[] { "archive", "default", "public-assets" }, provider.Names);

        var a = await publicAssets.CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "a.pdf", null, "Docs");
        var b = await archive.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "b.txt", null, "Docs");
        Assert.Equal(("public-assets", "archive"), (a.StorageName, b.StorageName));
        Assert.True(File.Exists(Path.Combine(_root, "public", a.FileLocation)));
        Assert.False(File.Exists(Path.Combine(_root, "archive", a.FileLocation)));
        Assert.False(await archive.ExistsAsync(new FileEntry { FileLocation = a.FileLocation }));

        // Entry-based routing: each entry goes back to its own root.
        Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await provider.Get(a).OpenReadAsync(a)));
        Assert.Equal(TestData.Text(), await TestData.ReadAllAsync(await provider.Get(StoredFileRef.From(b)).OpenReadAsync(b)));
        Assert.Same(provider.Get("default"), provider.Get(new FileEntry { StorageName = null }));
    }

    [Fact]
    public async Task Two_named_S3_instances_do_not_overwrite_each_other()
    {
        var s3 = new FakeS3();
        var services = new ServiceCollection();
        services.AddSingleton<IS3ClientFactory>(s3);
        services.AddKitStackStorage()
            .AddS3("hrm-docs", o => { o.MainTarget.BucketName = "erp-private"; o.MainTarget.Prefix = "hrm/employees/documents"; })
            .AddS3("fms-images", o => o.MainTarget.BucketName = "erp-images");
        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        var hrm = await provider.Get("hrm-docs").CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "cv.pdf", null, "Hr");
        var fms = await provider.Get("fms-images").CreateAsync<Invoice>(new MemoryStream(TestData.Png()), "car.png", null, "Fleet");

        Assert.StartsWith("hrm/employees/documents/", hrm.FileLocation, StringComparison.Ordinal);
        Assert.True(s3.Objects.ContainsKey(("erp-private", hrm.FileLocation)));
        Assert.True(s3.Objects.ContainsKey(("erp-images", fms.FileLocation)));
        Assert.False(s3.Objects.ContainsKey(("erp-images", hrm.FileLocation)));
        Assert.Equal(TestData.Png(), await TestData.ReadAllAsync(await provider.Get(fms).OpenReadAsync(fms)));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKitStackStorage().AddS3("x", o => o.MainTarget.BucketName = "a").AddS3("X", o => o.MainTarget.BucketName = "b"));
    }

    [Fact]
    public async Task Named_instances_bind_from_configuration_with_their_own_policy()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Upload:MaxBytes"] = "1000000",
            ["Storage:Instances:avatars:Local:Path"] = Path.Combine(_root, "avatars"),
            ["Storage:Instances:avatars:Upload:MaxBytes"] = "10",
            ["Storage:Instances:avatars:Upload:AllowedContentTypes:0"] = "image/*",
        }).Build();
        var services = new ServiceCollection();
        services.AddKitStackStorage(config).AddFake().AddLocal("avatars");
        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        await Assert.ThrowsAsync<StorageValidationException>(() => provider.Get("avatars").CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "A"));
        await Assert.ThrowsAsync<StorageValidationException>(() => provider.Get("avatars").CreateAsync<Invoice>(new MemoryStream(TestData.Png()), "a.png", null, "A"));
        // The default instance keeps the global policy.
        Assert.NotNull(await provider.Get("default").CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "A"));
        Assert.True(Directory.Exists(Path.Combine(_root, "avatars")));
    }

    [Fact]
    public async Task Encrypted_and_plaintext_instances_read_each_others_entries_through_the_provider()
    {
        var key = TestData.NewKey();
        var services = new ServiceCollection();
        services.AddKitStackStorage(o => o.MasterEncryptionKey = key)
            .AddFake("secure")
            .AddFake("open")
            .ConfigureInstance("open", o => o.DisableEncryption = true)
            .AddFake("other-key")
            .ConfigureInstance("other-key", o => o.MasterEncryptionKey = TestData.NewKey());
        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        var secret = await provider.Get("secure").CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "s.pdf", null, "Docs");
        var open = await provider.Get("open").CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "o.pdf", null, "Docs");
        var other = await provider.Get("other-key").CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "k.pdf", null, "Docs");

        Assert.True(secret.Encrypted);
        Assert.False(open.Encrypted);
        Assert.True(other.Encrypted);
        Assert.NotEqual(TestData.Pdf(), sp.GetRequiredKeyedService<IFakeFileStore>("secure").ListFiles().Single().Content);
        Assert.Equal(TestData.Pdf(), sp.GetRequiredKeyedService<IFakeFileStore>("open").ListFiles().Single().Content);

        foreach (var entry in new[] { secret, open, other })
            Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await provider.Get(entry).OpenReadAsync(entry)));

        // Encrypted instances hand out no direct URLs, and say so in their capabilities.
        Assert.Null(await provider.Get(secret).GetUrlAsync(secret));
        Assert.Equal(StorageCapabilities.Overwrite, provider.GetCapabilities("secure"));
        Assert.True(provider.GetCapabilities("open").HasFlag(StorageCapabilities.PublicUrls));
    }

    [Fact]
    public async Task Per_call_policy_can_only_tighten_the_instance_policy()
    {
        var services = new ServiceCollection();
        services.AddKitStackStorage(o => { o.Upload.MaxBytes = 100; o.Upload.AllowedExtensions.Add(".pdf"); o.Upload.AllowedExtensions.Add(".txt"); }).AddFake();
        await using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<IFileStorageManager>();

        StorageWriteOptions Options(string name, UploadPolicy policy) => new() { FileName = name, Folder = "docs", Policy = policy };

        var looser = new UploadPolicy { MaxBytes = 10_000 };
        looser.AllowedExtensions.Add(".png");
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.CreateAsync(Options("a.bin", looser), new MemoryStream(TestData.Random(50))));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.CreateAsync(Options("a.txt", new UploadPolicy { MaxBytes = 10_000 }), new MemoryStream(TestData.Random(150))));

        var stricter = new UploadPolicy { MaxBytes = 20 };
        stricter.AllowedContentTypes.Add("application/pdf");
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.CreateAsync(Options("a.txt", stricter), new MemoryStream(TestData.Text())));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.CreateAsync(Options("a.pdf", new UploadPolicy { MaxBytes = 5 }), new MemoryStream(TestData.Pdf())));
        var ok = new UploadPolicy();
        ok.AllowedContentTypes.Add("text/*");
        Assert.Equal("text/plain", (await storage.CreateAsync(Options("a.txt", ok), new MemoryStream(TestData.Text()))).ContentType);
    }

    [Fact]
    public async Task Non_generic_create_uses_the_explicit_folder_metadata_and_link()
    {
        await using var sp = new ServiceCollection().AddKitStackStorage().AddFake().AddFake("hrm").Services.BuildServiceProvider();
        var storage = sp.GetRequiredService<IFileStorageManagerProvider>().Get("hrm");

        var (entry, variants) = await storage.CreateWithVariantsAsync(new StorageWriteOptions
        {
            FileName = "Passport Scan.png",
            DeclaredContentType = "image/png",
            Category = "HRM",
            Folder = @"hrm\employees/ documents ",
            Metadata = new Dictionary<string, string> { ["Source"] = "portal", [FileEntryMetadataKeys.Folder] = "spoofed" },
            Link = new StorageEntityLink { EntityName = "Employee", EntityId = "17", Notes = "passport" },
        }, new MemoryStream(TestData.Png()));

        Assert.StartsWith("hrm/employees/documents/", entry.FileLocation, StringComparison.Ordinal);
        Assert.EndsWith("-Passport-Scan.png", entry.FileLocation, StringComparison.Ordinal);
        Assert.Equal(("HRM", "hrm", "portal", "hrm/employees/documents", "image/png"),
            (entry.Category, entry.StorageName, entry.Metadata!["Source"], entry.Metadata[FileEntryMetadataKeys.Folder], entry.Metadata[FileEntryMetadataKeys.DeclaredContentType]));
        Assert.Equal(("Employee", "17", "passport"), (entry.RelatedEntities!.Single().EntityName, entry.RelatedEntities!.Single().EntityId, entry.RelatedEntities!.Single().Notes));
        Assert.All(variants, v =>
        {
            Assert.Equal("hrm", v.StorageName);
            Assert.StartsWith("hrm/employees/documents/", v.FileLocation, StringComparison.Ordinal);
            Assert.Equal("17", v.RelatedEntities!.Single().EntityId);
        });

        // Without a folder the generic layout applies with the given entity name.
        var plain = await storage.CreateAsync(new StorageWriteOptions { FileName = "a.pdf", Category = "Billing", EntityName = "Invoice" }, new MemoryStream(TestData.Pdf()));
        Assert.StartsWith("Billing/Invoice/", plain.FileLocation, StringComparison.Ordinal);
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.CreateAsync(new StorageWriteOptions { FileName = "a.pdf", Category = "Billing" }, new MemoryStream(TestData.Pdf())));
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("docs/../../secrets")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\\server\share")]
    [InlineData("C:/Windows")]
    [InlineData("~/home")]
    [InlineData("docs/./x")]
    public async Task Explicit_folder_traversal_attempts_are_rejected(string folder)
    {
        var root = Path.Combine(_root, "traversal");
        await using var sp = new ServiceCollection().AddKitStackStorage().AddLocal(o => o.Path = root).Services.BuildServiceProvider();
        var storage = sp.GetRequiredService<IFileStorageManager>();
        await Assert.ThrowsAsync<StorageValidationException>(() =>
            storage.CreateAsync(new StorageWriteOptions { FileName = "x.txt", Folder = folder, EntityName = "E", Category = "C" }, new MemoryStream(TestData.Text())));
        Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Health_probe_and_health_checks_cover_static_instances()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddKitStackStorage()
            .AddLocal(o => o.Path = Path.Combine(_root, "h-default"))
            .AddLocal("reports", o => o.Path = Path.Combine(_root, "h-reports"))
            .AddKitStackStorageHealthChecks("storage");
        await using var sp = services.BuildServiceProvider();

        var probe = sp.GetRequiredService<IStorageHealthProbe>();
        Assert.Equal(HealthStatus.Healthy, (await probe.CheckAsync("reports")).Status);
        Assert.Equal(HealthStatus.Healthy, (await probe.CheckAsync("default")).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await probe.CheckAsync("missing")).Status);

        var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Contains("kitstack_local", report.Entries.Keys);
        Assert.Contains("kitstack_storage_reports", report.Entries.Keys);
        Assert.Contains("kitstack_storage_default", report.Entries.Keys);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task Capabilities_are_reported_per_provider_type_and_instance()
    {
        var s3 = new FakeS3();
        var services = new ServiceCollection();
        services.AddSingleton<KitStack.Storage.S3.Services.IS3ClientFactory>(s3);
        services.AddKitStackStorage()
            .AddLocal(o => o.Path = Path.Combine(_root, "c"))
            .AddLocal("cdn", o => { o.Path = Path.Combine(_root, "cdn"); o.PublicBaseUrl = "https://cdn.example.com"; })
            .AddS3("sse", o => { o.MainTarget.BucketName = "b"; o.MainTarget.UseServerSideEncryption = true; });
        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileStorageManagerProvider>();

        Assert.Equal(StorageCapabilities.Overwrite, provider.GetCapabilities("default"));
        Assert.Equal(StorageCapabilities.Overwrite | StorageCapabilities.PublicUrls, provider.GetCapabilities("cdn"));
        Assert.True(provider.GetCapabilities("sse").HasFlag(StorageCapabilities.ServerSideEncryption | StorageCapabilities.SignedUrls));
        Assert.Throws<StorageInstanceNotFoundException>(() => provider.GetCapabilities("nope"));
        Assert.False(provider.TryGet("nope", out _));
    }

    [Fact]
    public void Storage_options_merge_keeps_old_keys_readable()
    {
        var oldKey = TestData.NewKey();
        var global = new StorageOptions { MasterEncryptionKey = TestData.NewKey() };
        global.PreviousEncryptionKeys.Add(oldKey);
        var upload = new UploadPolicy { MaxBytes = 5 };
        var merged = StorageOptionsOverrides.Merge(global, new StorageOptionsOverrides { DisableEncryption = true, Upload = upload }, "x");

        Assert.Null(merged.MasterEncryptionKey);
        Assert.Equal(new[] { global.MasterEncryptionKey, oldKey }, merged.PreviousEncryptionKeys);
        Assert.Equal(("x", 5L), (merged.InstanceName, merged.Upload.MaxBytes));
        Assert.NotSame(upload, merged.Upload);
        Assert.Same(global.MasterEncryptionKey, StorageOptionsOverrides.Merge(global, null, "y").MasterEncryptionKey);
    }
}
