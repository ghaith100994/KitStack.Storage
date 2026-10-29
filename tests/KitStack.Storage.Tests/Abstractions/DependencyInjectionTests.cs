using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.Fakes.Contracts;
using KitStack.Fakes.Extensions;
using KitStack.Storage.Azure.Options;
using KitStack.Storage.Local.Extensions;
using KitStack.Storage.Sftp.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace KitStack.Storage.Tests.Abstractions;

public class DependencyInjectionTests
{
    [Fact]
    public async Task Fake_provider_runs_the_shared_pipeline_and_exposes_its_store()
    {
        using var sp = new ServiceCollection().AddKitStackStorage().AddFake().Services.BuildServiceProvider();
        var storage = sp.GetRequiredService<IFileStorageManager>();
        var fake = sp.GetRequiredService<IFakeFileStore>();

        var (primary, variants) = await storage.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");

        Assert.Equal(3, fake.ListFiles().Count);
        Assert.True(fake.TryGetFile(primary.FileLocation, out var stored));
        Assert.Same(primary, stored!.FileEntry);
        Assert.Equal(2, variants.Count);
        Assert.Equal(TestData.Png(), await TestData.ReadAllAsync(await storage.OpenReadAsync(primary)));

        await storage.DeleteAsync(primary);
        Assert.Empty(fake.ListFiles());
    }

    [Fact]
    public async Task Fake_size_limit_applies()
    {
        using var sp = new ServiceCollection().AddKitStackStorage().AddFake(o => o.MaxFileSizeBytes = 5).Services.BuildServiceProvider();
        await Assert.ThrowsAsync<StorageValidationException>(() =>
            sp.GetRequiredService<IFileStorageManager>().CreateAsync<Invoice>(new MemoryStream(TestData.Random(6)), "a.bin", null, "Docs"));
    }

    [Fact]
    public void Configuration_binds_policy_and_provider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Local",
            ["Storage:Upload:MaxBytes"] = "1024",
            ["Storage:Upload:AllowedContentTypes:0"] = "image/*",
            ["Storage:Local:Path"] = Path.Combine(Path.GetTempPath(), "kitstack-di-" + Guid.NewGuid().ToString("N")),
        }).Build();

        var builder = new ServiceCollection().AddKitStackStorage(config);
        Assert.True(builder.IsProvider("local"));
        builder.AddLocal();

        using var sp = builder.Services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
        Assert.Equal(1024, options.Upload.MaxBytes);
        Assert.Contains("image/*", options.Upload.AllowedContentTypes);
        Assert.NotNull(sp.GetRequiredService<IFileStorageManager>());
    }

    [Fact]
    public void Invalid_encryption_key_fails_options_validation()
    {
        using var sp = new ServiceCollection().AddKitStackStorage(o => o.MasterEncryptionKey = "too-short").Services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<StorageOptions>>().Value);
    }

    [Fact]
    public void Tracking_requires_a_provider_first()
        => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKitStackStorage().AddFileEntryTracking());

    [Fact]
    public void Sftp_requires_host_key_verification()
    {
        var options = new SftpOptions { Host = "h", Username = "u", Password = "p" };
        Assert.Contains(Validate(options), r => r.MemberNames.Contains(nameof(SftpOptions.HostKeyFingerprint)));

        options.HostKeyFingerprint = "SHA256:abc";
        Assert.Empty(Validate(options));
    }

    [Theory]
    [InlineData("files", true)]
    [InlineData("Files", false)]
    [InlineData("a", false)]
    [InlineData("my--files", false)]
    public void Azure_container_names_are_validated(string container, bool valid)
        => Assert.Equal(valid, Validate(new AzureBlobOptions { ContainerName = container, ConnectionString = "x" }).Count == 0);

    private static List<ValidationResult> Validate(object options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
