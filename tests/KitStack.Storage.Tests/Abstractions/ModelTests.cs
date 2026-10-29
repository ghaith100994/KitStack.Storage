using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Extensions;
using KitStack.Abstractions.Models;
using KitStack.Abstractions.Options;

namespace KitStack.Storage.Tests.Abstractions;

public class UploadPolicyTests
{
    [Fact]
    public void Empty_policy_allows_everything()
    {
        var policy = new UploadPolicy();
        Assert.True(policy.IsContentTypeAllowed("application/x-anything"));
        Assert.True(policy.IsExtensionAllowed(""));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/JPEG", true)]
    [InlineData("application/pdf", true)]
    [InlineData("application/pdfx", false)]
    [InlineData("imagex/png", false)]
    [InlineData("text/plain", false)]
    public void Content_types_match_exactly_or_by_wildcard(string type, bool allowed)
    {
        var policy = new UploadPolicy();
        policy.AllowedContentTypes.Add("image/*");
        policy.AllowedContentTypes.Add("application/pdf");
        Assert.Equal(allowed, policy.IsContentTypeAllowed(type));
    }

    [Theory]
    [InlineData(".PDF", true)]
    [InlineData("pdf", true)]
    [InlineData(".txt", false)]
    [InlineData("", false)]
    public void Extensions_match_with_or_without_the_dot(string extension, bool allowed)
    {
        var policy = new UploadPolicy();
        policy.AllowedExtensions.Add(".pdf");
        Assert.Equal(allowed, policy.IsExtensionAllowed(extension));
    }
}

public class FileEntryExtensionsTests
{
    private sealed class WithId { public int Id { get; set; } = 5; }
    private sealed class Order { public string OrderId { get; set; } = "ORD-1"; }
    private sealed class NoId { public string Name { get; set; } = "x"; }
    private sealed class NullId { public string? Id { get; set; } }
    private sealed class BlankId { public string Id { get; set; } = " "; }

    [Fact]
    public void Links_by_Id_or_TypeNameId_once()
    {
        var entry = new FileEntry { RelatedEntities = null };

        entry.LinkToEntity(new WithId(), notes: "avatar");
        entry.LinkToEntity(new WithId());
        entry.LinkToEntity(new Order(), "Orders", markAsUsed: false);

        Assert.Equal(2, entry.RelatedEntities!.Count);
        var first = entry.RelatedEntities.First();
        Assert.Equal(("5", "WithId", "avatar", true), (first.EntityId, first.EntityName, first.Notes, first.IsUsed));
        var order = entry.RelatedEntities.Last();
        Assert.Equal(("ORD-1", "Orders", false), (order.EntityId, order.EntityName, order.IsUsed));
    }

    [Fact]
    public void Entities_without_a_usable_id_are_rejected()
    {
        var entry = new FileEntry();
        Assert.Throws<StorageValidationException>(() => entry.LinkToEntity(new NoId()));
        Assert.Throws<StorageValidationException>(() => entry.LinkToEntity(new NullId()));
        Assert.Throws<StorageValidationException>(() => entry.LinkToEntity(new BlankId()));
        Assert.Throws<StorageValidationException>(() => entry.LinkToEntity(null!));
        Assert.Throws<StorageValidationException>(() => ((FileEntry)null!).LinkToEntity(new WithId()));
    }

    [Fact]
    public void Relations_are_copied_without_duplicates()
    {
        var source = new FileEntry();
        source.LinkToEntity(new WithId());
        source.LinkToEntity(new Order());
        var target = new FileEntry { RelatedEntities = null };
        target.LinkToEntity(new WithId());

        target.CopyRelationsFrom(source);
        target.CopyRelationsFrom(null);
        target.CopyRelationsFrom(new FileEntry());

        Assert.Equal(["WithId", "Order"], target.RelatedEntities!.Select(r => r.EntityName));
    }
}

public class StoredFileRefTests
{
    [Fact]
    public void Round_trips_the_fields_storage_needs()
    {
        var entry = new FileEntry
        {
            FileLocation = "a/b/c.PDF",
            FileName = "c.PDF",
            ContentType = "application/pdf",
            Size = 42,
            Checksum = "abc",
            StorageProvider = "S3",
            Encrypted = true,
        };

        var reference = StoredFileRef.From(entry);
        var rebuilt = reference.ToFileEntry();

        Assert.Equal(entry.Id, reference.FileEntryId);
        Assert.Equal((entry.Id, "a/b/c.PDF", "c.PDF", "application/pdf", 42L, "abc", "S3", true, ".pdf", "original"),
            (rebuilt.Id, rebuilt.FileLocation, rebuilt.FileName, rebuilt.ContentType, rebuilt.Size, rebuilt.Checksum, rebuilt.StorageProvider, rebuilt.Encrypted, rebuilt.FileExtension, rebuilt.VariantType));
        Assert.Equal(reference, StoredFileRef.From(entry));
        Assert.Throws<ArgumentNullException>(() => StoredFileRef.From(null!));
    }

    [Fact]
    public void FileEntry_defaults_are_safe()
    {
        var entry = new FileEntry();
        Assert.Equal("application/octet-stream", entry.ContentType);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.NotNull(entry.RelatedEntities);
    }

    [Fact]
    public void Checksum_has_a_default_implementation_for_older_IFileEntry_types()
    {
        KitStack.Abstractions.Interfaces.IFileEntry legacy = new LegacyEntry();
        legacy.Checksum = "ignored";
        Assert.Null(legacy.Checksum);
    }

    private sealed class LegacyEntry : KitStack.Abstractions.Interfaces.IFileEntry
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = "";
        public string FileLocation { get; set; } = "";
        public string? StoragePath { get; set; }
        public string? Category { get; set; }
        public long Size { get; set; }
        public string ContentType { get; set; } = "";
        public string? FileExtension { get; set; }
        public IDictionary<string, string>? Metadata { get; set; }
        public DateTimeOffset UploadedTime { get; set; }
        public string? VariantType { get; set; }
        public bool Encrypted { get; set; }
        public string? OriginalFileName { get; set; }
        public string? StorageProvider { get; set; }
        public DateTimeOffset? LastAccessedTime { get; set; }
        public bool IsDeleted { get; set; }
        public ICollection<FileRelatedEntity>? RelatedEntities { get; set; }
    }
}

public class ExceptionTests
{
    [Fact]
    public void All_storage_exceptions_derive_from_StorageException_and_keep_inner_exceptions()
    {
        var inner = new InvalidOperationException("inner");
        StorageException[] all =
        [
            new StorageException(), new StorageException("m"), new StorageException("m", inner),
            new StorageNotFoundException(), new StorageNotFoundException("m"), new StorageNotFoundException("m", inner),
            new StorageValidationException(), new StorageValidationException("m"), new StorageValidationException("m", inner),
            new StorageConfigurationException(), new StorageConfigurationException("m"), new StorageConfigurationException("m", inner),
        ];
        Assert.Equal(4, all.Count(e => e.InnerException == inner));
        Assert.Equal(8, all.Count(e => e.Message == "m"));
    }
}
