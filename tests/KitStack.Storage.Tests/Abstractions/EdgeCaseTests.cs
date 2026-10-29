using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using Microsoft.AspNetCore.Http;

namespace KitStack.Storage.Tests.Abstractions;

[Collection(TempFileCollection.Name)]
public class EdgeCaseTests
{
    [Fact]
    public async Task Replace_from_a_form_file_and_with_nothing_to_replace()
    {
        var storage = new RecordingStorageManager();
        var old = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("old")), "a.txt", null, "Docs");
        var form = new FormFile(new MemoryStream(TestData.Text("new")), 0, 3, "f", "a.txt") { Headers = new HeaderDictionary() };

        var replaced = await storage.ReplaceAsync<Invoice>(old, form, "Docs");
        var fresh = await storage.ReplaceAsync<Invoice>(null, new MemoryStream(TestData.Text("x")), "b.txt", null, "Docs");

        Assert.False(storage.Files.ContainsKey(old.FileLocation));
        Assert.Equal(TestData.Text("new"), storage.Files[replaced.FileLocation]);
        Assert.True(storage.Files.ContainsKey(fresh.FileLocation));
        Assert.Single(storage.Deletes);
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((KitStack.Abstractions.Interfaces.IFileStorageManager)null!).ReplaceAsync<Invoice>(null, form, "Docs"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.ReplaceAsync<Invoice>(null, (IFormFile)null!, "Docs"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.OpenReadAsync((StoredFileRef)null!));
    }

    [Fact]
    public async Task Corrupt_encrypted_content_fails_without_leaking_the_plaintext_buffer()
    {
        var storage = new RecordingStorageManager(new StorageOptions { MasterEncryptionKey = TestData.NewKey() });
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Random(SpoolThreshold + 10)), "big.bin", null, "Docs");
        storage.Files[entry.FileLocation][^5] ^= 0xFF;
        var before = Directory.GetFiles(Path.GetTempPath(), "kitstack-*.tmp").Length;

        await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync(entry));

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "kitstack-*.tmp").Length);
    }

    [Fact]
    public async Task Large_files_round_trip_through_temp_spooling_with_encryption()
    {
        var storage = new RecordingStorageManager(new StorageOptions { MasterEncryptionKey = TestData.NewKey() });
        var data = TestData.Random(SpoolThreshold + 1234);

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(data), "big.bin", null, "Docs");

        Assert.Equal(TestData.Sha256(data), entry.Checksum);
        Assert.Equal(data, await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
    }

    private const int SpoolThreshold = KitStack.Abstractions.IO.SpooledContent.MemoryThreshold;
}
