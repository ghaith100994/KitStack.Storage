using KitStack.Abstractions.Options;
using KitStack.Fakes.Extensions;
using KitStack.Fakes.Options;
using KitStack.Fakes.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests.Fakes;

public class InMemoryFileStorageManagerTests
{
    [Fact]
    public async Task Urls_overwrite_and_clear()
    {
        var fake = new InMemoryFileStorageManager();
        var entry = await fake.CreateAsync<Invoice>(new MemoryStream(TestData.Text("a")), "a.txt", null, "Docs");

        Assert.Equal($"fake://storage/{entry.FileLocation}", (await fake.GetUrlAsync(entry))!.ToString());

        await fake.OverwriteAsync(entry, new MemoryStream(TestData.Text("bb")));
        Assert.True(fake.TryGetFile(entry.FileLocation, out var stored));
        Assert.Equal(TestData.Text("bb"), stored!.Content);
        Assert.Same(entry, stored.FileEntry);

        fake.Clear();
        Assert.Empty(fake.ListFiles());
        Assert.False(await fake.ExistsAsync(entry));
    }

    [Fact]
    public async Task Overwriting_an_unknown_location_creates_it()
    {
        var fake = new InMemoryFileStorageManager();
        var entry = new KitStack.Abstractions.Models.FileEntry { FileLocation = "restored/a.txt" };

        await fake.OverwriteAsync(entry, new MemoryStream(TestData.Text()));

        Assert.True(fake.TryGetFile("restored/a.txt", out var stored));
        Assert.Equal("restored/a.txt", stored!.FileEntry.FileLocation);
    }

    [Fact]
    public async Task Delay_is_applied_and_cancellable()
    {
        var fake = new InMemoryFileStorageManager(MsOptions.Create(new FakeOptions { OperationDelayMs = 5_000 }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs", cts.Token));
    }

    [Fact]
    public async Task Fake_limit_combines_with_the_storage_policy()
    {
        var storage = new StorageOptions { MasterEncryptionKey = TestData.NewKey() };
        storage.Upload.MaxBytes = 5;
        storage.Upload.AllowedContentTypes.Add("text/*");
        storage.Upload.AllowedExtensions.Add(".txt");
        storage.PreviousEncryptionKeys.Add(TestData.NewKey());
        var fake = new InMemoryFileStorageManager(MsOptions.Create(new FakeOptions { MaxFileSizeBytes = 100 }), MsOptions.Create(storage));

        // The smaller limit (storage: 5) wins; the other settings are kept.
        await Assert.ThrowsAsync<KitStack.Abstractions.Exceptions.StorageValidationException>(() =>
            fake.CreateAsync<Invoice>(new MemoryStream(TestData.Text("123456")), "a.txt", null, "Docs"));
        await Assert.ThrowsAsync<KitStack.Abstractions.Exceptions.StorageValidationException>(() =>
            fake.CreateAsync<Invoice>(new MemoryStream(TestData.Text("1")), "a.csv", null, "Docs"));
        var entry = await fake.CreateAsync<Invoice>(new MemoryStream(TestData.Text("12345")), "a.txt", null, "Docs");
        Assert.True(entry.Encrypted);
    }

    [Fact]
    public void Configuration_binds_fake_options()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MaxFileSizeBytes"] = "9", ["OperationDelayMs"] = "3" }).Build();
        using var sp = new ServiceCollection().AddInMemoryFakeStorage(config).BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FakeOptions>>().Value;
        Assert.Equal((9L, 3, true), (options.MaxFileSizeBytes!.Value, options.OperationDelayMs, options.PreserveInMemory));
    }
}
