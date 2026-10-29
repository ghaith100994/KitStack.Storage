using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using Microsoft.AspNetCore.Http;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests;

public sealed class LocalFileStorageManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kitstack-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LocalFileStorageManager Create(Action<StorageOptions>? storage = null, Action<LocalOptions>? local = null)
    {
        var localOptions = new LocalOptions { Path = _root };
        local?.Invoke(localOptions);
        var storageOptions = new StorageOptions();
        storage?.Invoke(storageOptions);
        return new LocalFileStorageManager(MsOptions.Create(localOptions), MsOptions.Create(storageOptions));
    }

    [Fact]
    public async Task Stream_upload_resolves_content_type_and_checksum_then_reads_back()
    {
        var storage = Create();
        var pdf = TestData.Pdf();

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "march invoice.pdf", "text/html", "Billing");

        Assert.Equal("application/pdf", entry.ContentType);
        Assert.Equal("text/html", entry.Metadata!["DeclaredContentType"]);
        Assert.Equal(TestData.Sha256(pdf), entry.Checksum);
        Assert.Equal(pdf.Length, entry.Size);
        Assert.Equal("march invoice.pdf", entry.FileName);
        Assert.StartsWith("Billing/Invoice/Others/", entry.FileLocation, StringComparison.Ordinal);
        Assert.EndsWith("-march-invoice.pdf", entry.FileLocation, StringComparison.Ordinal);
        Assert.False(entry.Encrypted);
        Assert.True(File.Exists(Path.Combine(_root, entry.FileLocation)));

        Assert.True(await storage.ExistsAsync(entry));
        Assert.Equal(pdf, await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
    }

    [Fact]
    public async Task Form_file_upload_uses_the_same_pipeline()
    {
        var storage = Create();
        var png = TestData.Png();
        var form = new FormFile(new MemoryStream(png), 0, png.Length, "file", "avatar.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };

        var entry = await storage.CreateAsync<Invoice>(form, "Users");

        Assert.Equal("image/png", entry.ContentType);
        Assert.Contains("/Images/", entry.FileLocation, StringComparison.Ordinal);
        Assert.Equal(png, await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_is_idempotent()
    {
        var storage = Create();
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");

        await storage.DeleteAsync(entry);

        Assert.True(entry.IsDeleted);
        Assert.False(await storage.ExistsAsync(entry));
        await storage.DeleteAsync(entry);
        await Assert.ThrowsAsync<StorageNotFoundException>(() => storage.OpenReadAsync(entry));
    }

    [Fact]
    public async Task Reads_and_deletes_cannot_escape_the_base_directory()
    {
        var storage = Create();
        var outside = new FileEntry { FileLocation = "../outside.txt" };
        var sibling = new FileEntry { FileLocation = _root + "-other/secret.txt" };

        await Assert.ThrowsAsync<StorageValidationException>(() => storage.OpenReadAsync(outside));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.DeleteAsync(outside));
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.OpenReadAsync(sibling));
    }

    [Fact]
    public async Task Category_with_traversal_is_rejected()
        => await Assert.ThrowsAsync<StorageValidationException>(() =>
            Create().CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "../../etc"));

    [Fact]
    public async Task Url_requires_a_public_base_url()
    {
        var entry = await Create().CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a b.txt", null, "Docs");
        Assert.Null(await Create().GetUrlAsync(entry));

        var withBase = Create(local: o => o.PublicBaseUrl = "https://app.example.com/files/");
        var url = await withBase.GetUrlAsync(entry);
        Assert.Equal($"https://app.example.com/files/{entry.FileLocation}", url!.ToString());
    }

    [Fact]
    public async Task Encryption_key_encrypts_at_rest_and_decrypts_on_read()
    {
        var storage = Create(o => o.MasterEncryptionKey = TestData.NewKey(), o => o.PublicBaseUrl = "https://app/files");
        var pdf = TestData.Pdf();

        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(pdf), "secret.pdf", null, "Legal");

        Assert.True(entry.Encrypted);
        Assert.Equal("application/pdf", entry.ContentType);
        Assert.Equal(TestData.Sha256(pdf), entry.Checksum);
        Assert.Equal(pdf.Length, entry.Size);

        var onDisk = await File.ReadAllBytesAsync(Path.Combine(_root, entry.FileLocation));
        Assert.StartsWith("KSENC", System.Text.Encoding.ASCII.GetString(onDisk, 0, 5), StringComparison.Ordinal);
        Assert.DoesNotContain("%PDF", System.Text.Encoding.ASCII.GetString(onDisk), StringComparison.Ordinal);

        Assert.Equal(pdf, await TestData.ReadAllAsync(await storage.OpenReadAsync(entry)));
        // A direct URL would serve ciphertext.
        Assert.Null(await storage.GetUrlAsync(entry));
        // Detected from the header even when the caller lost the flag.
        var rebuilt = new FileEntry { FileLocation = entry.FileLocation, Encrypted = false };
        Assert.Equal(pdf, await TestData.ReadAllAsync(await storage.OpenReadAsync(rebuilt)));
    }

    [Fact]
    public async Task Encrypted_files_cannot_be_read_without_a_key()
    {
        var entry = await Create(o => o.MasterEncryptionKey = TestData.NewKey())
            .CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");

        await Assert.ThrowsAsync<StorageConfigurationException>(() => Create().OpenReadAsync(entry));
    }

    [Fact]
    public async Task Upload_policy_is_enforced_before_anything_is_written()
    {
        var limited = Create(o => o.Upload.MaxBytes = 10);
        await Assert.ThrowsAsync<StorageValidationException>(() =>
            limited.CreateAsync<Invoice>(new MemoryStream(TestData.Random(11)), "big.bin", null, "Docs"));

        var imagesOnly = Create(o => o.Upload.AllowedContentTypes.Add("image/*"));
        await Assert.ThrowsAsync<StorageValidationException>(() =>
            imagesOnly.CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "doc.pdf", null, "Docs"));
        // A PDF renamed to .png is still a PDF.
        await Assert.ThrowsAsync<StorageValidationException>(() =>
            imagesOnly.CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "doc.png", null, "Docs"));
        Assert.NotNull(await imagesOnly.CreateAsync<Invoice>(new MemoryStream(TestData.Png()), "ok.png", null, "Docs"));

        var pdfOnly = Create(o => o.Upload.AllowedExtensions.Add(".pdf"));
        await Assert.ThrowsAsync<StorageValidationException>(() =>
            pdfOnly.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs"));

        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Variants_are_created_and_deleted_with_the_original()
    {
        var storage = Create(local: o => o.ImageProcessing.AdditionalSizes.Add(new ImageSizeOption { SizeName = "small", MaxWidth = 50, MaxHeight = 50 }));

        var (primary, variants) = await storage.CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Png()), "photo.png", "image/png", "Gallery");

        Assert.Equal(["compressed", "thumbnail", "small"], variants.Select(v => v.VariantType));
        Assert.All(variants, v =>
        {
            Assert.Equal("image/jpeg", v.ContentType);
            Assert.NotNull(v.Checksum);
            Assert.Equal(primary.Id.ToString("N"), v.Metadata!["OriginalId"]);
        });
        foreach (var v in variants)
            Assert.True(await storage.ExistsAsync(v));
        Assert.Equal(variants[0].FileLocation, primary.Metadata!["CompressedPath"]);

        await storage.DeleteAsync(primary);

        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Non_images_get_no_variants()
    {
        var (_, variants) = await Create().CreateWithVariantsAsync<Invoice>(new MemoryStream(TestData.Pdf()), "a.pdf", null, "Docs");
        Assert.Empty(variants);
    }

    [Fact]
    public async Task Replace_stores_the_new_file_and_deletes_the_old_one()
    {
        var storage = Create();
        var old = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("v1")), "a.txt", null, "Docs");

        var replacement = await storage.ReplaceAsync<Invoice>(old, new MemoryStream(TestData.Text("v2")), "a.txt", null, "Docs");

        Assert.False(await storage.ExistsAsync(old));
        Assert.Equal(TestData.Text("v2"), await TestData.ReadAllAsync(await storage.OpenReadAsync(replacement)));
    }

    [Fact]
    public async Task StoredFileRef_round_trips_to_the_file()
    {
        var storage = Create();
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Pdf()), "a.pdf", null, "Docs");
        var reference = StoredFileRef.From(entry);

        Assert.Equal(entry.Checksum, reference.Checksum);
        Assert.Equal(TestData.Pdf(), await TestData.ReadAllAsync(await storage.OpenReadAsync(reference)));
        await storage.DeleteAsync(reference);
        Assert.False(await storage.ExistsAsync(reference));
    }
}
