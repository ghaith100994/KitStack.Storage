using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Storage.Azure.Extensions;
using KitStack.Storage.Azure.HealthChecks;
using KitStack.Storage.Azure.Options;
using KitStack.Storage.Azure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests.Azure;

public class AzureBlobFileStorageManagerTests
{
    private readonly BlobServiceClient _service = Substitute.For<BlobServiceClient>();
    private readonly BlobContainerClient _container = Substitute.For<BlobContainerClient>();
    private readonly Dictionary<string, BlobClient> _blobs = [];
    private readonly Dictionary<string, byte[]> _content = [];
    private readonly List<(string Name, BlobUploadOptions Options)> _uploads = [];

    public AzureBlobFileStorageManagerTests()
    {
        _service.GetBlobContainerClient("files").Returns(_container);
        _service.AccountName.Returns("account");
        _container.Name.Returns("files");
        _container.GetBlobClient(Arg.Any<string>()).Returns(ci => Blob(ci.Arg<string>()));
    }

    private BlobClient Blob(string name)
    {
        if (_blobs.TryGetValue(name, out var existing))
            return existing;

        var blob = Substitute.For<BlobClient>();
        blob.Name.Returns(name);
        blob.Uri.Returns(new Uri($"https://account.blob.core.windows.net/files/{name}"));
        blob.UploadAsync(Arg.Any<Stream>(), Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            using var ms = new MemoryStream();
            ci.Arg<Stream>().CopyTo(ms);
            _content[name] = ms.ToArray();
            _uploads.Add((name, ci.Arg<BlobUploadOptions>()));
            return Response.FromValue(BlobsModelFactory.BlobContentInfo(default, default, default, default, default), Substitute.For<Response>());
        });
        blob.DownloadStreamingAsync(Arg.Any<BlobDownloadOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
            _content.TryGetValue(name, out var body)
                ? Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(body)), Substitute.For<Response>()))
                : Task.FromException<Response<BlobDownloadStreamingResult>>(new RequestFailedException(404, "BlobNotFound")));
        blob.ExistsAsync(Arg.Any<CancellationToken>()).Returns(_ => Response.FromValue(_content.ContainsKey(name), Substitute.For<Response>()));
        blob.DeleteIfExistsAsync(Arg.Any<DeleteSnapshotsOption>(), Arg.Any<BlobRequestConditions>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Response.FromValue(_content.Remove(name), Substitute.For<Response>()));
        _blobs[name] = blob;
        return blob;
    }

    private AzureBlobFileStorageManager Create(Action<AzureBlobOptions>? configure = null, StorageOptions? storage = null)
    {
        var options = new AzureBlobOptions { ContainerName = "files", Prefix = "app/" };
        configure?.Invoke(options);
        return new AzureBlobFileStorageManager(MsOptions.Create(options), storage is null ? null : MsOptions.Create(storage), serviceClient: _service);
    }

    private int Calls(object substitute, string method)
        => substitute is NSubstitute.Core.ICallRouterProvider ? ((object)substitute).ReceivedCalls().Count(c => c.GetMethodInfo().Name == method) : 0;

    [Fact]
    public async Task Uploads_with_prefix_content_type_and_tier_then_reads_back()
    {
        var storage = Create(o => o.AccessTier = "cool");
        var pdf = TestData.Pdf();

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "a.pdf", null, "Legal");

        var (name, options) = Assert.Single(_uploads);
        Assert.StartsWith("app/Legal/Invoice/Others/", name, StringComparison.Ordinal);
        Assert.Equal(name, entry.FileLocation);
        Assert.Equal("application/pdf", options.HttpHeaders.ContentType);
        Assert.Equal(AccessTier.Cool, options.AccessTier);
        Assert.Equal("Azure", entry.StorageProvider);
        Assert.Equal(0, Calls(_container, nameof(BlobContainerClient.CreateIfNotExistsAsync)));

        Assert.True(await storage.ExistsAsync(entry));
        Assert.Equal(pdf, await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
    }

    [Fact]
    public async Task Variants_use_the_variant_tier_and_are_deleted_with_the_original()
    {
        var storage = Create(o =>
        {
            o.AccessTier = "Hot";
            o.VariantAccessTier = "ARCHIVE";
            o.ImageProcessing = new ImageProcessingOptions();
        });

        var (primary, variants) = await storage.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");

        Assert.Equal([AccessTier.Hot, AccessTier.Archive, AccessTier.Archive], _uploads.Select(u => u.Options.AccessTier));
        Assert.Equal("image/jpeg", _uploads[1].Options.HttpHeaders.ContentType);
        Assert.Equal(2, variants.Count);

        await storage.DeleteAsync(primary);
        Assert.Empty(_content);
    }

    [Fact]
    public async Task Variant_tier_falls_back_to_the_original_tier_and_none_means_account_default()
    {
        await Create(o => { o.AccessTier = "Cold"; o.ImageProcessing = new ImageProcessingOptions { CreateCompressed = false }; })
            .CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "G");
        await Create().CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");

        Assert.Equal([AccessTier.Cold, AccessTier.Cold, null], _uploads.Select(u => u.Options.AccessTier));
    }

    [Fact]
    public async Task Container_is_created_once_when_configured()
    {
        var storage = Create(o => o.CreateContainerIfNotExists = true);

        await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "D");
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "b.txt", null, "D");
        await storage.OverwriteAsync(entry, new MemoryStream(TestData.Text("new")));

        Assert.Equal(1, Calls(_container, nameof(BlobContainerClient.CreateIfNotExistsAsync)));
        Assert.Equal(TestData.Text("new"), _content[entry.FileLocation]);
        Assert.Same(_container, storage.Container);
    }

    [Fact]
    public async Task Missing_and_archived_blobs()
    {
        var storage = Create();
        await Assert.ThrowsAsync<StorageNotFoundException>(() => storage.OpenReadAsync(new FileEntry { FileLocation = "missing" }));

        Blob("archived").DownloadStreamingAsync(Arg.Any<BlobDownloadOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException(409, "archived", BlobErrorCode.BlobArchived.ToString(), null));
        var ex = await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync(new FileEntry { FileLocation = "archived" }));
        Assert.Contains("Archive tier", ex.Message, StringComparison.Ordinal);
        Assert.False(await storage.ExistsAsync(new FileEntry { FileLocation = "missing" }));
    }

    [Fact]
    public async Task Sas_url_signed_locally_when_the_client_has_the_account_key()
    {
        var blob = Blob("app/a.pdf");
        blob.CanGenerateSasUri.Returns(true);
        blob.GenerateSasUri(Arg.Any<BlobSasBuilder>()).Returns(ci => new Uri($"https://signed/?se={ci.Arg<BlobSasBuilder>().ExpiresOn:O}&p={ci.Arg<BlobSasBuilder>().Permissions}"));

        var url = await Create(o => o.SasUrlExpirationSeconds = 60).GetUrlAsync(new FileEntry { FileLocation = "app/a.pdf" });

        var builder = (BlobSasBuilder)blob.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(BlobClient.GenerateSasUri)).GetArguments()[0]!;
        Assert.Equal(("files", "app/a.pdf", "b", "r"), (builder.BlobContainerName, builder.BlobName, builder.Resource, builder.Permissions));
        Assert.InRange((builder.ExpiresOn - DateTimeOffset.UtcNow).TotalSeconds, 50, 61);
        Assert.StartsWith("https://signed/", url!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sas_url_falls_back_to_a_user_delegation_key_or_null()
    {
        var entry = new FileEntry { FileLocation = "app/a.pdf" };
        Blob("app/a.pdf").CanGenerateSasUri.Returns(false);
        var key = BlobsModelFactory.UserDelegationKey("oid", "tid", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "b", "2023-11-03", Convert.ToBase64String(new byte[32]));
        _service.GetUserDelegationKeyAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(key, Substitute.For<Response>()));

        var delegated = await Create().GetUrlAsync(entry, TimeSpan.FromMinutes(5));
        Assert.Contains("skoid=oid", delegated!.Query, StringComparison.Ordinal);
        Assert.Contains("sig=", delegated.Query, StringComparison.Ordinal);

        _service.GetUserDelegationKeyAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException(403, "no permission"));
        Assert.Null(await Create().GetUrlAsync(entry));
    }

    [Fact]
    public async Task Real_client_with_an_account_key_signs_offline()
    {
        var options = new AzureBlobOptions { ContainerName = "files", ConnectionString = "UseDevelopmentStorage=true" };
        var storage = new AzureBlobFileStorageManager(MsOptions.Create(options));

        var url = await storage.GetUrlAsync(new FileEntry { FileLocation = "a b.pdf" }, TimeSpan.FromMinutes(1));

        Assert.Equal("127.0.0.1", url!.Host);
        Assert.Contains("/devstoreaccount1/files/a%20b.pdf", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("sp=r", url.Query, StringComparison.Ordinal);
        Assert.Contains("sig=", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_tier_can_be_changed()
    {
        var storage = Create();
        await storage.SetAccessTierAsync(new FileEntry { FileLocation = "app/a.pdf" }, AccessTier.Cool, RehydratePriority.High);

        var call = Blob("app/a.pdf").ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(BlobClient.SetAccessTierAsync));
        Assert.Equal(AccessTier.Cool, call.GetArguments()[0]);
        Assert.Equal(RehydratePriority.High, call.GetArguments()[2]);
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.SetAccessTierAsync(new FileEntry(), AccessTier.Hot));
        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.SetAccessTierAsync(null!, AccessTier.Hot));
    }

    [Fact]
    public void Service_client_comes_from_DI_connection_string_or_uri()
    {
        Assert.Equal("devstoreaccount1", AzureBlobFileStorageManager.CreateServiceClient(new AzureBlobOptions { ConnectionString = "UseDevelopmentStorage=true" }).AccountName);
        Assert.Equal("acct", AzureBlobFileStorageManager.CreateServiceClient(new AzureBlobOptions { ServiceUri = new Uri("https://acct.blob.core.windows.net?sv=x") }).AccountName);
        Assert.Throws<StorageConfigurationException>(() => AzureBlobFileStorageManager.CreateServiceClient(new AzureBlobOptions()));
    }

    [Fact]
    public async Task Health_check_reports_container_state()
    {
        var check = new AzureBlobHealthCheck(MsOptions.Create(new AzureBlobOptions { ContainerName = "files" }), _service);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("azure", check, HealthStatus.Degraded, null) };

        _container.ExistsAsync(Arg.Any<CancellationToken>()).Returns(Response.FromValue(true, Substitute.For<Response>()));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(context)).Status);

        _container.ExistsAsync(Arg.Any<CancellationToken>()).Returns(Response.FromValue(false, Substitute.For<Response>()));
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(context)).Status);

        _container.ExistsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new RequestFailedException(403, "denied"));
        var failed = await check.CheckHealthAsync(context);
        Assert.Equal(HealthStatus.Degraded, failed.Status);
        Assert.IsType<RequestFailedException>(failed.Exception);
    }

    [Theory]
    [InlineData("files", null, null, 900, true)]
    [InlineData("files", "hot", "Cool", 900, true)]
    [InlineData("files", "warm", null, 900, false)]
    [InlineData("files", null, "frozen", 900, false)]
    [InlineData("files", null, null, 0, false)]
    [InlineData("ab", null, null, 900, false)]
    [InlineData("-files", null, null, 900, false)]
    public void Options_are_validated(string container, string? tier, string? variantTier, int sasSeconds, bool valid)
    {
        var options = new AzureBlobOptions { ContainerName = container, AccessTier = tier, VariantAccessTier = variantTier, SasUrlExpirationSeconds = sasSeconds };
        Assert.Equal(valid, Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true));
    }

    [Fact]
    public void Registration_binds_Storage_Azure_and_prefers_a_DI_service_client()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Azure:ContainerName"] = "files",
            ["Storage:Azure:Prefix"] = "p",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton(_service);
        services.AddKitStackStorage(config).AddAzureBlob(o => o.SasUrlExpirationSeconds = 30);

        using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<AzureBlobFileStorageManager>();
        Assert.Same(manager, sp.GetRequiredService<IFileStorageManager>());
        Assert.Same(_container, manager.Container);
        Assert.NotNull(sp.GetRequiredService<AzureBlobHealthCheck>());

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => new ServiceCollection().AddAzureBlobStorageManager(new AzureBlobOptions { ContainerName = "X" }));
        using var direct = new ServiceCollection().AddAzureBlobStorageManager(new AzureBlobOptions { ContainerName = "files", ConnectionString = "UseDevelopmentStorage=true" }).BuildServiceProvider();
        Assert.IsType<AzureBlobFileStorageManager>(direct.GetRequiredService<IFileStorageManager>());
    }
}
