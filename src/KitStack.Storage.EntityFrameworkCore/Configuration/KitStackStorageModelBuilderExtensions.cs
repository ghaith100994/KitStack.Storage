using System.Linq.Expressions;
using System.Text.Json;
using KitStack.Abstractions.Models;
using KitStack.Storage.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KitStack.Storage.EntityFrameworkCore.Configuration;

/// <summary>
/// Model configuration for the KitStack storage tables and for <see cref="StoredFileRef"/> owned types.
/// </summary>
public static class KitStackStorageModelBuilderExtensions
{
    /// <summary>
    /// Add the <c>FileEntries</c> and <c>FileEntryLinks</c> tables to a model. Call it from
    /// <c>OnModelCreating</c> of the context that should hold file metadata.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="context">
    /// The context being configured. Used to detect SQLite, which cannot compare <see cref="DateTimeOffset"/>
    /// values in SQL; there they are stored as sortable integers.
    /// </param>
    /// <param name="schema">Optional schema for both tables.</param>
    public static ModelBuilder ApplyKitStackStorage(this ModelBuilder modelBuilder, DbContext? context = null, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var sqlite = context?.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

        modelBuilder.Entity<FileEntryRecord>(b =>
        {
            b.ToTable("FileEntries", schema);
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.FileName).HasMaxLength(255).IsRequired();
            b.Property(e => e.FileLocation).HasMaxLength(1024).IsRequired();
            b.Property(e => e.StoragePath).HasMaxLength(1024);
            b.Property(e => e.Category).HasMaxLength(200);
            b.Property(e => e.ContentType).HasMaxLength(255).IsRequired();
            b.Property(e => e.Checksum).HasMaxLength(64).IsFixedLength(false);
            b.Property(e => e.FileExtension).HasMaxLength(16);
            b.Property(e => e.VariantType).HasMaxLength(100);
            b.Property(e => e.OriginalFileName).HasMaxLength(1024);
            b.Property(e => e.StorageProvider).HasMaxLength(50);
            b.Property(e => e.Metadata).HasConversion(MetadataConverter, MetadataComparer);

            if (sqlite)
            {
                b.Property(e => e.UploadedTime).HasConversion(new DateTimeOffsetToBinaryConverter());
                b.Property(e => e.LastAccessedTime).HasConversion(new DateTimeOffsetToBinaryConverter());
                b.Property(e => e.DeletedTime).HasConversion(new DateTimeOffsetToBinaryConverter());
            }

            b.HasIndex(e => e.FileLocation);
            b.HasIndex(e => e.ParentId);
            b.HasIndex(e => new { e.IsDeleted, e.UploadedTime });

            b.OwnsMany(e => e.RelatedEntities, links =>
            {
                links.ToTable("FileEntryLinks", schema);
                links.WithOwner().HasForeignKey("FileEntryId");
                links.Property<int>("Id");
                links.HasKey("Id");
                links.Property(l => l.EntityName).HasMaxLength(200).IsRequired();
                links.Property(l => l.EntityId).HasMaxLength(200).IsRequired();
                links.Property(l => l.Notes).HasMaxLength(500);
                if (sqlite)
                    links.Property(l => l.LinkedOn).HasConversion(new DateTimeOffsetToBinaryConverter());
                links.HasIndex(l => new { l.EntityName, l.EntityId });
            });
            b.Navigation(e => e.RelatedEntities).AutoInclude();
        });

        return modelBuilder;
    }

    /// <summary>
    /// Map a <see cref="StoredFileRef"/> property as an owned type (columns <c>{Navigation}_FileEntryId</c>,
    /// <c>{Navigation}_Location</c>, ... on the entity's own table).
    /// </summary>
    public static EntityTypeBuilder<TEntity> OwnsStoredFile<TEntity>(
        this EntityTypeBuilder<TEntity> builder, Expression<Func<TEntity, StoredFileRef?>> navigation)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.OwnsOne(navigation, ConfigureStoredFile);
        return builder;
    }

    /// <summary>
    /// Map a collection of <see cref="StoredFileRef"/> as owned rows in their own table.
    /// </summary>
    public static EntityTypeBuilder<TEntity> OwnsStoredFiles<TEntity>(
        this EntityTypeBuilder<TEntity> builder, Expression<Func<TEntity, IEnumerable<StoredFileRef>?>> navigation, string tableName, string? schema = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.OwnsMany(navigation, owned =>
        {
            owned.ToTable(tableName, schema);
            owned.WithOwner();
            ConfigureStoredFile(owned);
        });
        return builder;
    }

    private static void ConfigureStoredFile<TOwner>(OwnedNavigationBuilder<TOwner, StoredFileRef> owned)
        where TOwner : class
    {
        owned.Property(f => f.Location).HasMaxLength(1024);
        owned.Property(f => f.FileName).HasMaxLength(255);
        owned.Property(f => f.ContentType).HasMaxLength(255);
        owned.Property(f => f.Checksum).HasMaxLength(64);
        owned.Property(f => f.StorageProvider).HasMaxLength(50);
        owned.HasIndex(f => f.FileEntryId);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private static readonly ValueConverter<IDictionary<string, string>?, string?> MetadataConverter = new(
        v => v == null || v.Count == 0 ? null : JsonSerializer.Serialize(v, JsonOptions),
        v => string.IsNullOrEmpty(v) ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions));

    private static readonly ValueComparer<IDictionary<string, string>?> MetadataComparer = new(
        (a, b) => a == null ? b == null || b.Count == 0 : b == null ? a.Count == 0 : a.Count == b.Count && !a.Except(b).Any(),
        v => v == null ? 0 : v.Aggregate(0, (hash, kv) => hash ^ HashCode.Combine(kv.Key, kv.Value)),
        v => v == null ? null : new Dictionary<string, string>(v));
}
