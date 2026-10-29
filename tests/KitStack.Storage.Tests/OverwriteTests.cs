using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests;

public sealed class OverwriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kitstack-overwrite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LocalFileStorageManager Create(Action<StorageOptions>? storage = null)
    {
        var options = new StorageOptions();
        storage?.Invoke(options);
        return new LocalFileStorageManager(MsOptions.Create(new LocalOptions { Path = _root }), MsOptions.Create(options));
    }

    [Fact]
    public async Task Overwrite_replaces_content_in_place_and_updates_the_entry()
    {
        var storage = Create();
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("v1")), "notes.txt", null, "Docs");
        var location = entry.FileLocation;

        await storage.OverwriteAsync(entry, new MemoryStream(TestData.Text("version two")));

        Assert.Equal(location, entry.FileLocation);
        Assert.Equal(TestData.Sha256(TestData.Text("version two")), entry.Checksum);
        Assert.Equal(TestData.Text("version two").Length, entry.Size);
        Assert.Equal(TestData.Text("version two"), await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
    }

    [Fact]
    public async Task Overwrite_restores_a_missing_file_at_its_recorded_location()
    {
        var storage = Create();
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "a.pdf", null, "Archive");
        File.Delete(Path.Combine(_root, entry.FileLocation));

        await storage.OverwriteAsync(entry, new MemoryStream(TestData.Pdf()));

        Assert.True(await storage.ExistsAsync(entry));
    }

    [Fact]
    public async Task Overwrite_encrypts_when_a_key_is_configured_and_respects_the_policy()
    {
        var key = TestData.NewKey();
        var plain = await Create().CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");
        var encrypting = Create(o =>
        {
            o.MasterEncryptionKey = key;
            o.Upload.MaxBytes = 100;
        });

        await encrypting.OverwriteAsync(plain, new MemoryStream(TestData.Text("now encrypted")));
        Assert.True(plain.Encrypted);
        Assert.Equal(TestData.Text("now encrypted"), await TestData.ReadAllAsync(await encrypting.OpenReadAsync(plain)));

        await Assert.ThrowsAsync<StorageValidationException>(() => encrypting.OverwriteAsync(plain, new MemoryStream(TestData.Random(101))));
        await Assert.ThrowsAsync<StorageValidationException>(() => Create().OverwriteAsync(new FileEntry { FileLocation = "../x.txt" }, new MemoryStream(TestData.Text())));
    }
}
