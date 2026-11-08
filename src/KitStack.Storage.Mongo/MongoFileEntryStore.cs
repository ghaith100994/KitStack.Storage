using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
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
            map.MapMember(d => d.PreviousVersionId).SetSerializer(new NullableSerializer<Guid>(new GuidSerializer(GuidRepresentation.Standard)));
            map.MapMember(d => d.SupersededById).SetSerializer(new NullableSerializer<Guid>(new GuidSerializer(GuidRepresentation.Standard)));
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
                  & Filter.Eq(d => d.SupersededById, null)
                  & Filter.Lt(d => d.UploadedTime, uploadedBefore.UtcDateTime)
                  & Filter.Not(Filter.ElemMatch(d => d.Links, Builders<FileLinkDocument>.Filter.Eq(l => l.IsUsed, true))))
            .SortBy(d => d.UploadedTime)
            .Limit(maxCount)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => (IFileEntry)d.ToFileEntry()).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(
        DateTimeOffset uploadedBefore, int maxCount, string? storageName, bool includeSupersededVersions = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        var collection = await ReadyAsync().ConfigureAwait(false);
        var filter = ByStorage(storageName)
                     & Filter.Eq(d => d.IsDeleted, false)
                     & Filter.Eq(d => d.ParentId, null)
                     & Filter.Lt(d => d.UploadedTime, uploadedBefore.UtcDateTime)
                     & Filter.Not(Filter.ElemMatch(d => d.Links, Builders<FileLinkDocument>.Filter.Eq(l => l.IsUsed, true)));
        if (!includeSupersededVersions)
            filter &= Filter.Eq(d => d.SupersededById, null);
        var documents = await collection.Find(filter).SortBy(d => d.UploadedTime).Limit(maxCount).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => (IFileEntry)d.ToFileEntry()).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IFileEntry>> FindManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
            return [];
        var collection = await ReadyAsync().ConfigureAwait(false);
        var documents = await collection.Find(Filter.In(d => d.Id, list)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => (IFileEntry)d.ToFileEntry()).ToList();
    }

    /// <inheritdoc/>
    public async Task<IFileEntry?> FindByLocationAsync(string fileLocation, string? storageName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileLocation);
        var collection = await ReadyAsync().ConfigureAwait(false);
        var document = await collection
            .Find(ByStorage(storageName) & Filter.Eq(d => d.FileLocation, fileLocation) & Filter.Eq(d => d.IsDeleted, false))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?.ToFileEntry();
    }

    /// <inheritdoc/>
    public async Task<FileEntryPage> QueryAsync(FileEntryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var collection = await ReadyAsync().ConfigureAwait(false);
        var filter = BuildFilter(query);
        var total = await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
        var sort = Builders<FileEntryDocument>.Sort;
        SortDefinition<FileEntryDocument> order = query.SortBy switch
        {
            FileEntrySortField.Size => query.Descending ? sort.Descending(d => d.Size) : sort.Ascending(d => d.Size),
            FileEntrySortField.FileName => query.Descending ? sort.Descending(d => d.FileName) : sort.Ascending(d => d.FileName),
            _ => query.Descending ? sort.Descending(d => d.UploadedTime) : sort.Ascending(d => d.UploadedTime),
        };
        var documents = await collection.Find(filter).Sort(order.Ascending(d => d.Id)).Skip(query.Skip).Limit(query.Take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new FileEntryPage { Items = documents.Select(d => (IFileEntry)d.ToFileEntry()).ToList(), TotalCount = total, Skip = query.Skip, Take = query.Take };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FileUsage>> GetUsageAsync(FileUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var collection = await ReadyAsync().ConfigureAwait(false);
        var filter = ByStorage(query.StorageName);
        if (!query.IncludeDeleted)
            filter &= Filter.Eq(d => d.IsDeleted, false);
        if (query.Category is { } category)
            filter &= Filter.Eq(d => d.Category, category);

        var byStorage = query.GroupBy.HasFlag(FileUsageGrouping.StorageName);
        var byCategory = query.GroupBy.HasFlag(FileUsageGrouping.Category);
        var key = new BsonDocument();
        if (byStorage)
            key["s"] = new BsonDocument("$ifNull", new BsonArray { "$StorageName", StorageInstance.Default });
        if (byCategory)
            key["c"] = new BsonDocument("$ifNull", new BsonArray { "$Category", BsonNull.Value });

        // Aggregated by the server: $group with $sum.
        var rows = await collection.Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                { "_id", key.ElementCount == 0 ? BsonNull.Value : key },
                { "count", new BsonDocument("$sum", 1) },
                { "bytes", new BsonDocument("$sum", "$Size") },
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var usage = rows.Select(r =>
        {
            var id = r["_id"] as BsonDocument;
            return new FileUsage(
                byStorage ? id?["s"].AsString : null,
                byCategory && id?["c"] is { IsBsonNull: false } c ? c.AsString : null,
                r["count"].ToInt64(),
                r["bytes"].ToInt64());
        }).OrderBy(u => u.StorageName).ThenBy(u => u.Category).ToList();
        return usage.Count == 0 && query.GroupBy == FileUsageGrouping.None ? [new FileUsage(null, null, 0, 0)] : usage;
    }

    /// <inheritdoc/>
    public async Task<FileDuplicatePage> FindDuplicatesAsync(FileDuplicateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MinCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Take);
        var collection = await ReadyAsync().ConfigureAwait(false);
        var filter = ByStorage(query.StorageName) & Filter.Eq(d => d.IsDeleted, false) & Filter.Eq(d => d.ParentId, null) & Filter.Ne(d => d.Checksum, null);
        if (query.Category is { } category)
            filter &= Filter.Eq(d => d.Category, category);

        var key = new BsonDocument("k", "$Checksum");
        if (!query.AcrossStorageInstances)
            key["s"] = new BsonDocument("$ifNull", new BsonArray { "$StorageName", StorageInstance.Default });

        var grouped = collection.Aggregate()
            .Match(filter)
            .SortBy(d => d.UploadedTime)
            .Group(new BsonDocument
            {
                { "_id", key },
                { "count", new BsonDocument("$sum", 1) },
                { "bytes", new BsonDocument("$sum", "$Size") },
                { "ids", new BsonDocument("$push", "$_id") },
            })
            .Match(new BsonDocument("count", new BsonDocument("$gte", query.MinCount)));

        var facet = await grouped
            .Facet(
                AggregateFacet.Create("total", PipelineDefinition<BsonDocument, BsonDocument>.Create(new BsonDocument("$count", "n"))),
                AggregateFacet.Create("page", PipelineDefinition<BsonDocument, BsonDocument>.Create(
                    new BsonDocument("$sort", new BsonDocument { { "count", -1 }, { "_id.k", 1 } }),
                    new BsonDocument("$skip", query.Skip),
                    new BsonDocument("$limit", query.Take))))
            .FirstAsync(cancellationToken).ConfigureAwait(false);

        var totalRows = facet.Facets.First(f => f.Name == "total").Output<BsonDocument>();
        var pageRows = facet.Facets.First(f => f.Name == "page").Output<BsonDocument>();
        var groups = pageRows.Select(r =>
        {
            var id = r["_id"].AsBsonDocument;
            var ids = r["ids"].AsBsonArray.Select(v => v.AsGuid).ToList();
            return new FileDuplicateGroup(id["k"].AsString, id.Contains("s") ? id["s"].AsString : null, r["count"].ToInt32(), r["bytes"].ToInt64(), ids);
        }).ToList();
        return new FileDuplicatePage { Groups = groups, TotalGroups = totalRows.Count == 0 ? 0 : totalRows[0]["n"].ToInt64() };
    }

    /// <inheritdoc/>
    public async Task RelocateAsync(IReadOnlyCollection<FileRelocation> relocations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relocations);
        if (relocations.Count == 0)
            return;
        var collection = await ReadyAsync().ConfigureAwait(false);
        var writes = relocations.Select(r =>
        {
            var update = Update.Set(d => d.StorageName, r.StorageName)
                .Set(d => d.FileLocation, r.FileLocation)
                .Set(d => d.StoragePath, r.StoragePath)
                .Set(d => d.Encrypted, r.Encrypted);
            if (r.StorageProvider is not null)
                update = update.Set(d => d.StorageProvider, r.StorageProvider);
            if (r.Metadata is not null)
                update = update.Set(d => d.Metadata, new Dictionary<string, string>(r.Metadata));
            return (WriteModel<FileEntryDocument>)new UpdateOneModel<FileEntryDocument>(Filter.Eq(d => d.Id, r.Id), update);
        }).ToList();
        // One ordered bulk write; every update is idempotent, so a transfer can be re-run after a failure.
        await collection.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SupersedeAsync(Guid previousId, Guid newId, CancellationToken cancellationToken = default)
    {
        var collection = await ReadyAsync().ConfigureAwait(false);
        var documents = await collection.Find(Filter.In(d => d.Id, [previousId, newId])).ToListAsync(cancellationToken).ConfigureAwait(false);
        var previous = documents.FirstOrDefault(d => d.Id == previousId) ?? throw new StorageNotFoundException($"File entry '{previousId}' was not found.");
        var current = documents.FirstOrDefault(d => d.Id == newId) ?? throw new StorageNotFoundException($"File entry '{newId}' was not found.");

        var moved = previous.Links.Where(l => l.IsUsed).ToList();
        var links = current.Links.ToList();
        foreach (var link in moved)
        {
            var existing = links.FirstOrDefault(l => l.EntityName == link.EntityName && l.EntityId == link.EntityId);
            if (existing is null)
                links.Add(link);
            else
                existing.IsUsed = true;
        }

        // New version first: if the second update fails, the old version is still linked (never swept) and a re-run completes it.
        await collection.UpdateOneAsync(Filter.Eq(d => d.Id, newId),
            Update.Set(d => d.PreviousVersionId, previousId).Set(d => d.VersionNumber, previous.VersionNumber + 1).Set(d => d.Links, links),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await collection.UpdateOneAsync(Filter.Eq(d => d.Id, previousId),
            Update.Set(d => d.SupersededById, newId).Set(d => d.Links, previous.Links.Where(l => !l.IsUsed).ToList()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static FilterDefinition<FileEntryDocument> ByStorage(string? storageName)
    {
        if (storageName is null)
            return Filter.Empty;
        return StorageInstance.IsDefault(storageName)
            ? Filter.In(d => d.StorageName, [null, StorageInstance.Default])
            : Filter.Eq(d => d.StorageName, storageName.Trim());
    }

    private static FilterDefinition<FileEntryDocument> BuildFilter(FileEntryQuery query)
    {
        var filter = ByStorage(query.StorageName);
        if (!query.IncludeDeleted)
            filter &= Filter.Eq(d => d.IsDeleted, false);
        if (query.Category is { } category)
            filter &= Filter.Eq(d => d.Category, category);
        if (query.EntityName is not null || query.EntityId is not null)
        {
            var link = Builders<FileLinkDocument>.Filter.Eq(l => l.IsUsed, true);
            if (query.EntityName is { } entityName)
                link &= Builders<FileLinkDocument>.Filter.Eq(l => l.EntityName, entityName);
            if (query.EntityId is { } entityId)
                link &= Builders<FileLinkDocument>.Filter.Eq(l => l.EntityId, entityId);
            filter &= Filter.ElemMatch(d => d.Links, link);
        }

        if (!string.IsNullOrEmpty(query.ContentTypePrefix))
            filter &= Filter.Regex(d => d.ContentType, new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(query.ContentTypePrefix)));
        if (query.UploadedFrom is { } from)
            filter &= Filter.Gte(d => d.UploadedTime, from.UtcDateTime);
        if (query.UploadedTo is { } to)
            filter &= Filter.Lt(d => d.UploadedTime, to.UtcDateTime);
        var used = Filter.ElemMatch(d => d.Links, Builders<FileLinkDocument>.Filter.Eq(l => l.IsUsed, true));
        if (query.IsOrphan == true)
            filter &= Filter.Eq(d => d.ParentId, null) & Filter.Eq(d => d.SupersededById, null) & Filter.Not(used);
        else if (query.IsOrphan == false)
            filter &= used;
        if (query.Checksum is { } checksum)
            filter &= Filter.Eq(d => d.Checksum, checksum);
        if (query.OriginalsOnly)
            filter &= Filter.Eq(d => d.ParentId, null);
        if (query.OriginalId is { } originalId)
            filter &= Filter.Eq(d => d.ParentId, originalId);
        if (!query.IncludeSupersededVersions)
            filter &= Filter.Eq(d => d.SupersededById, null);
        return filter;
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
            new CreateIndexModel<FileEntryDocument>(keys.Ascending(d => d.StorageName).Ascending(d => d.IsDeleted)),
            new CreateIndexModel<FileEntryDocument>(keys.Ascending(d => d.StorageName).Ascending(d => d.Checksum)),
            new CreateIndexModel<FileEntryDocument>(keys.Ascending(d => d.SupersededById)),
        ]);
    }
}
