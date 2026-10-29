using KitStack.Abstractions.Interfaces;
using KitStack.Storage.Mongo.Documents;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace KitStack.Storage.Mongo;

/// <summary>
/// MongoDB implementation of <see cref="IFileEntryStore"/>: one document per stored file with its entity links
/// embedded. Indexes are created on first use.
/// </summary>
public sealed class MongoFileEntryStore : IFileEntryStore
{
    /// <summary>Default collection name.</summary>
    public const string DefaultCollectionName = "fileEntries";

    private static readonly FilterDefinitionBuilder<FileEntryDocument> Filter = Builders<FileEntryDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<FileEntryDocument> Update = Builders<FileEntryDocument>.Update;

    private readonly IMongoCollection<FileEntryDocument> _collection;
    private readonly Lazy<Task> _indexes;

    static MongoFileEntryStore()
    {
        // Guids as standard UUIDs (BSON subtype 4), independent of any global driver setting.
        BsonClassMap.TryRegisterClassMap<FileEntryDocument>(map =>
        {
            map.AutoMap();
            map.MapIdMember(d => d.Id).SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            map.MapMember(d => d.ParentId).SetSerializer(new NullableSerializer<Guid>(new GuidSerializer(GuidRepresentation.Standard)));
            map.SetIgnoreExtraElements(true);
        });
        BsonClassMap.TryRegisterClassMap<FileLinkDocument>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);
        });
    }

    /// <summary>Create a store over <paramref name="database"/>.<paramref name="collectionName"/>.</summary>
    public MongoFileEntryStore(IMongoDatabase database, string collectionName = DefaultCollectionName)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        _collection = database.GetCollection<FileEntryDocument>(collectionName);
        _indexes = new Lazy<Task>(CreateIndexesAsync);
    }

    /// <summary>The underlying collection, for queries this interface does not cover.</summary>
    public IMongoCollection<FileEntryDocument> Collection => _collection;

    /// <inheritdoc/>
    public async Task AddAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var collection = await ReadyAsync().ConfigureAwait(false);
        try
        {
            await collection.InsertOneAsync(FileEntryDocument.From(entry), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Already recorded.
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var collection = await ReadyAsync().ConfigureAwait(false);
        await collection.UpdateOneAsync(
            Filter.Eq(d => d.Id, entry.Id),
            Update.Set(d => d.Size, entry.Size)
                .Set(d => d.Checksum, entry.Checksum)
                .Set(d => d.ContentType, entry.ContentType)
                .Set(d => d.Encrypted, entry.Encrypted)
                .Set(d => d.LastAccessedTime, entry.LastAccessedTime?.UtcDateTime)
                .Set(d => d.Metadata, entry.Metadata is null ? null : new Dictionary<string, string>(entry.Metadata)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IFileEntry?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var collection = await ReadyAsync().ConfigureAwait(false);
        var document = await collection.Find(Filter.Eq(d => d.Id, id)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?.ToFileEntry();
    }

    /// <inheritdoc/>
    public async Task<IFileEntry?> FindByLocationAsync(string fileLocation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileLocation);
        var collection = await ReadyAsync().ConfigureAwait(false);
        var document = await collection
            .Find(Filter.Eq(d => d.FileLocation, fileLocation) & Filter.Eq(d => d.IsDeleted, false))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?.ToFileEntry();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IFileEntry>> ListByEntityAsync(string entityName, string entityId, bool includeUnused = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        var collection = await ReadyAsync().ConfigureAwait(false);

        var link = Builders<FileLinkDocument>.Filter.Eq(l => l.EntityName, entityName) & Builders<FileLinkDocument>.Filter.Eq(l => l.EntityId, entityId);
        if (!includeUnused)
            link &= Builders<FileLinkDocument>.Filter.Eq(l => l.IsUsed, true);

        var documents = await collection
            .Find(Filter.Eq(d => d.IsDeleted, false) & Filter.ElemMatch(d => d.Links, link))
            .SortBy(d => d.UploadedTime)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => (IFileEntry)d.ToFileEntry()).ToList();
    }

    /// <inheritdoc/>
    public async Task LinkAsync(Guid fileEntryId, string entityName, string entityId, string? notes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        var collection = await ReadyAsync().ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Re-activate a released link...
        var reactivated = await collection.UpdateOneAsync(
            Filter.Eq(d => d.Id, fileEntryId) & Filter.ElemMatch(d => d.Links, LinkFilter(entityName, entityId, isUsed: false)),
            Update.Set("Links.$.IsUsed", true).Set("Links.$.LinkedOn", now),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reactivated.ModifiedCount > 0)
            return;

        // ...or add it when the entity is not linked at all (the filter keeps concurrent calls from duplicating it).
        await collection.UpdateOneAsync(
            Filter.Eq(d => d.Id, fileEntryId) & Filter.Not(Filter.ElemMatch(d => d.Links, LinkFilter(entityName, entityId, isUsed: null))),
            Update.Push(d => d.Links, new FileLinkDocument { EntityName = entityName, EntityId = entityId, IsUsed = true, Notes = notes, LinkedOn = now }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UnlinkAsync(Guid fileEntryId, string entityName, string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        var collection = await ReadyAsync().ConfigureAwait(false);
        await collection.UpdateOneAsync(
            Filter.Eq(d => d.Id, fileEntryId) & Filter.ElemMatch(d => d.Links, LinkFilter(entityName, entityId, isUsed: true)),
            Update.Set("Links.$.IsUsed", false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MarkDeletedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var collection = await ReadyAsync().ConfigureAwait(false);
        // The original and the variants generated from it.
        await collection.UpdateManyAsync(
            (Filter.Eq(d => d.Id, id) | Filter.Eq(d => d.ParentId, id)) & Filter.Eq(d => d.IsDeleted, false),
            Update.Set(d => d.IsDeleted, true).Set(d => d.DeletedTime, DateTime.UtcNow),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(DateTimeOffset uploadedBefore, int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        var collection = await ReadyAsync().ConfigureAwait(false);
        var documents = await collection
            .Find(Filter.Eq(d => d.IsDeleted, false)
                  & Filter.Eq(d => d.ParentId, null)
                  & Filter.Lt(d => d.UploadedTime, uploadedBefore.UtcDateTime)
                  & Filter.Not(Filter.ElemMatch(d => d.Links, Builders<FileLinkDocument>.Filter.Eq(l => l.IsUsed, true))))
            .SortBy(d => d.UploadedTime)
            .Limit(maxCount)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => (IFileEntry)d.ToFileEntry()).ToList();
    }

    private static FilterDefinition<FileLinkDocument> LinkFilter(string entityName, string entityId, bool? isUsed)
    {
        var filter = Builders<FileLinkDocument>.Filter.Eq(l => l.EntityName, entityName) & Builders<FileLinkDocument>.Filter.Eq(l => l.EntityId, entityId);
        return isUsed is { } used ? filter & Builders<FileLinkDocument>.Filter.Eq(l => l.IsUsed, used) : filter;
    }

    private async Task<IMongoCollection<FileEntryDocument>> ReadyAsync()
    {
        await _indexes.Value.ConfigureAwait(false);
        return _collection;
    }

    private Task CreateIndexesAsync()
    {
        var keys = Builders<FileEntryDocument>.IndexKeys;
        return _collection.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<FileEntryDocument>(keys.Ascending(d => d.FileLocation)),
            new CreateIndexModel<FileEntryDocument>(keys.Ascending(d => d.ParentId)),
            new CreateIndexModel<FileEntryDocument>(keys.Ascending(d => d.IsDeleted).Ascending(d => d.UploadedTime)),
            new CreateIndexModel<FileEntryDocument>(keys.Ascending("Links.EntityName").Ascending("Links.EntityId")),
        ]);
    }
}
