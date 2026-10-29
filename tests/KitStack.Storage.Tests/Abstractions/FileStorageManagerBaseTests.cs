using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Abstractions.Security;
using Microsoft.AspNetCore.Http;

namespace KitStack.Storage.Tests.Abstractions;

public sealed class AttachableDocument : IFileAttachable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<IFileEntry> Attachments { get; } = [];
    public void AddFileAttachment(IFileEntry fileEntry) => Attachments.Add(fileEntry);
}

public class FileStorageManagerBaseTests
{
    [Fact]
    public async Task Writes_use_the_category_entity_and_type_folder_layout()
    {
        var storage = new RecordingStorageManager();

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Png()), "My Photo!.PNG", "image/png", "Sales/2026");

        var write = Assert.Single(storage.Writes);
        Assert.Equal("Sales/2026/Invoice/Images", write.Folder);
        Assert.Equal($"{entry.Id:N}-My-Photo.png", write.FileName);
        Assert.Equal("original", write.VariantType);
        Assert.Equal("image/png", write.ContentType);
        Assert.Equal(TestData.Png().Length, write.Length);
        Assert.Equal("My Photo!.PNG", entry.FileName);
        Assert.Equal(".png", entry.FileExtension);
        Assert.Equal("Recording", entry.StorageProvider);
        Assert.Equal("Sales/2026", entry.Category);
        Assert.Equal(7, entry.Id.Version);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected()
    {
        var storage = new RecordingStorageManager();

        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.CreateAsync<Invoice>((Stream)null!, "a.txt", null, "Docs"));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.CreateAsync<Invoice>(new MemoryStream(), " ", null, "Docs"));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.CreateAsync<Invoice>(new MemoryStream(), "a.txt", null, null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.CreateAsync<Invoice>((IFormFile)null!, "Docs"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.OpenReadAsync(null!));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.OpenReadAsync(new FileEntry()));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.DeleteAsync(new FileEntry { FileLocation = " " }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.GetUrlAsync(new FileEntry { FileLocation = "a" }, TimeSpan.Zero));
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public async Task Providers_without_urls_or_overwrite_report_it()
    {
        var storage = new RecordingStorageManager();
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");

        Assert.Null(await storage.GetUrlAsync(entry));
        await Assert.ThrowsAsync<NotSupportedException>(() => storage.OverwriteAsync(entry, new MemoryStream(TestData.Text())));
    }

    [Fact]
    public async Task Entity_overload_attaches_and_links_the_entry()
    {
        var storage = new RecordingStorageManager();
        var document = new AttachableDocument();
        var form = new FormFile(new MemoryStream(TestData.Pdf()), 0, TestData.Pdf().Length, "file", "a.pdf") { Headers = new HeaderDictionary() };

        var entry = await storage.CreateAsync(document, form, "Legal");

        Assert.Same(entry, Assert.Single(document.Attachments));
        var link = Assert.Single(entry.RelatedEntities!);
        Assert.Equal(document.Id.ToString(), link.EntityId);
        Assert.Equal("Legal", link.EntityName);
        Assert.True(link.IsUsed);
    }

    [Fact]
    public async Task Form_file_variants_overload_creates_variants()
    {
        var storage = new RecordingStorageManager(images: new ImageProcessingOptions { CreateCompressed = false });
        var png = TestData.Png();
        var form = new FormFile(new MemoryStream(png), 0, png.Length, "file", "p.png") { Headers = new HeaderDictionary() };

        var (primary, variants) = await storage.CreateWithVariantsAsync<Invoice>(form, "Gallery");

        var thumbnail = Assert.Single(variants);
        Assert.Equal("thumbnail", thumbnail.VariantType);
        Assert.Equal("Gallery/Invoice/Images/thumbnails", storage.Writes[1].Folder);
        Assert.Equal(thumbnail.FileLocation, primary.Metadata!["ThumbnailPath"]);
        Assert.False(primary.Metadata.ContainsKey("CompressedPath"));
    }

    [Fact]
    public async Task Variants_are_skipped_without_image_options_or_for_undecodable_images()
    {
        var withoutOptions = new RecordingStorageManager();
        var (_, none) = await withoutOptions.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");
        Assert.Empty(none);

        // PNG signature followed by garbage: resolved as image/png, but ImageSharp cannot decode it.
        var corrupt = TestData.Png()[..16].Concat(TestData.Random(200)).ToArray();
        var withOptions = new RecordingStorageManager(images: new ImageProcessingOptions());
        var (primary, variants) = await withOptions.CreateWithVariantsAsync<Invoice>(new MemoryStream(corrupt), "p.png", null, "Gallery");
        Assert.Equal("image/png", primary.ContentType);
        Assert.Empty(variants);
        Assert.Single(withOptions.Files);
    }

    [Fact]
    public async Task Size_variants_with_blank_names_are_ignored()
    {
        var images = new ImageProcessingOptions { CreateCompressed = false, CreateThumbnail = false };
        images.AdditionalSizes.Add(new ImageSizeOption { SizeName = " ", MaxWidth = 10, MaxHeight = 10 });
        images.AdditionalSizes.Add(new ImageSizeOption { SizeName = "Card View", MaxWidth = 64, MaxHeight = 64 });
        var storage = new RecordingStorageManager(images: images);

        var (primary, variants) = await storage.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "p.png", null, "Gallery");

        var card = Assert.Single(variants);
        Assert.Equal("Card View", card.VariantType);
        Assert.Equal("Gallery/Invoice/Images/Card-View", storage.Writes[1].Folder);
        Assert.Equal(card.FileLocation, primary.Metadata!["Variants"]);
    }

    [Fact]
    public async Task Deleting_an_original_deletes_its_recorded_variants_with_their_variant_types()
    {
        var storage = new RecordingStorageManager();
        var entry = new FileEntry
        {
            FileLocation = "a/b/x.png",
            VariantType = null,
            Metadata = new Dictionary<string, string>
            {
                ["CompressedPath"] = "a/b/compressed/x.jpg",
                ["ThumbnailPath"] = "a/b/thumbnails/x.jpg",
                ["Variants"] = "a/b/small/x.jpg; a/b/large/x.jpg",
            },
        };

        await storage.DeleteAsync(entry);

        Assert.Equal(
            [("a/b/x.png", "original"), ("a/b/compressed/x.jpg", "compressed"), ("a/b/thumbnails/x.jpg", "thumbnail"), ("a/b/small/x.jpg", "small"), ("a/b/large/x.jpg", "large")],
            storage.Deletes.Select(d => (d.Location, d.VariantType)));
        Assert.True(entry.IsDeleted);
    }

    [Fact]
    public async Task Deleting_a_variant_does_not_follow_metadata()
    {
        var storage = new RecordingStorageManager();
        await storage.DeleteAsync(new FileEntry { FileLocation = "t.jpg", VariantType = "thumbnail", Metadata = new Dictionary<string, string> { ["ThumbnailPath"] = "other.jpg" } });
        Assert.Equal(["t.jpg"], storage.Deletes.Select(d => d.Location));
    }

    [Fact]
    public async Task Encrypted_writes_are_labelled_octet_stream_and_decrypt_from_non_seekable_reads()
    {
        var storage = new RecordingStorageManager(new StorageOptions { MasterEncryptionKey = TestData.NewKey() }) { NonSeekableReads = true };
        var pdf = TestData.Pdf();

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "a.pdf", null, "Docs");

        Assert.Equal("application/octet-stream", storage.Writes[0].ContentType);
        Assert.True(storage.Writes[0].Length > pdf.Length);
        Assert.Equal(pdf, await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
    }

    [Fact]
    public async Task Plain_files_read_through_a_configured_encryptor_keep_their_bytes_from_non_seekable_streams()
    {
        var plain = new RecordingStorageManager();
        var entry = await plain.CreateAsync<Invoice>(new MemoryStream(TestData.Text("plain text")), "a.txt", null, "Docs");

        var keyed = new RecordingStorageManager(new StorageOptions { MasterEncryptionKey = TestData.NewKey() }) { NonSeekableReads = true };
        keyed.Files[entry.FileLocation] = plain.Files[entry.FileLocation];

        Assert.Equal(TestData.Text("plain text"), await TestData.ReadAllAsync(await keyed.OpenReadAsync(entry)));
    }

    [Fact]
    public async Task An_entry_flagged_encrypted_with_plain_content_is_an_error()
    {
        var storage = new RecordingStorageManager(new StorageOptions { MasterEncryptionKey = TestData.NewKey() });
        storage.Files["x.txt"] = TestData.Text();

        await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync(new FileEntry { FileLocation = "x.txt", Encrypted = true }));
    }

    [Fact]
    public async Task A_custom_encryptor_replaces_the_default()
    {
        var encryptor = new AesGcmFileContentEncryptor(TestData.NewKey());
        var storage = new RecordingStorageManager(encryptor: encryptor);

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");

        Assert.True(entry.Encrypted);
        Assert.True(encryptor.IsEncrypted(storage.Files[entry.FileLocation]));
    }

    [Fact]
    public async Task Reading_sets_last_access_and_honours_cancellation()
    {
        var storage = new RecordingStorageManager();
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");
        entry.LastAccessedTime = null;

        await (await storage.OpenReadAsync(entry)).DisposeAsync();
        Assert.NotNull(entry.LastAccessedTime);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs", cancelled.Token));
    }
}
