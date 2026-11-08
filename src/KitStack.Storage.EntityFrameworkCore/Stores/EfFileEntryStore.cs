using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;
using KitStack.Storage.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// EF Core implementation of <see cref="IFileEntryStore"/> over any context whose model includes
/// <see cref="FileEntryRecord"/> (see <c>ApplyKitStackStorage</c>).
/// </summary>
/// <remarks>
/// Every operation runs on its own context instance (from <see cref="IDbContextFactory{TContext}"/> when
/// registered, otherwise from a new DI scope), so calling the store never saves unrelated pending changes
/// of the application's scoped context. That also makes the store safe to register as a singleton.
/// </remarks>
public sealed class EfFileEntryStore<TContext>(IServiceProvider services) : IFileEntryStore
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext>? _factory = services.GetService<IDbContextFactory<TContext>>();
    private readonly IServiceScopeFactory _scopes = services.GetRequiredService<IServiceScopeFactory>();

    /// <inheritdoc/>
    public Task AddAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RunAsync(async db =>
        {
            if (await db.Set<FileEntryRecord>().AnyAsync(r => r.Id == entry.Id, cancellationToken).ConfigureAwait(false))
                return;
            db.Add(FileEntryRecord.From(entry));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    public Task UpdateAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RunAsync(async db =>
        {
            var record = await db.Set<FileEntryRecord>().FirstOrDefaultAsync(r => r.Id == entry.Id, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return;
            record.Size = entry.Size;
            record.Checksum = entry.Checksum;
            record.ContentType = entry.ContentType;
            record.Encrypted = entry.Encrypted;
            record.LastAccessedTime = entry.LastAccessedTime;
            record.Metadata = entry.Metadata is null ? null : new Dictionary<string, string>(entry.Metadata);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    public Task<IFileEntry?> FindAsync(DefaultIdType id, CancellationToken cancellationToken = default)
        => QueryAsync<IFileEntry?>(async db =>
            await db.Set<FileEntryRecord>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc/>
    public Task<IFileEntry?> FindByLocationAsync(string fileLocation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileLocation);
        return QueryAsync<IFileEntry?>(async db =>
            await db.Set<FileEntryRecord>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.FileLocation == fileLocation && !r.IsDeleted, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> ListByEntityAsync(string entityName, string entityId, bool includeUnused = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        return QueryAsync<IReadOnlyList<IFileEntry>>(async db =>
            await db.Set<FileEntryRecord>().AsNoTracking()
                .Where(r => !r.IsDeleted && r.RelatedEntities!.Any(l =>
                    l.EntityName == entityName && l.EntityId == entityId && (includeUnused || l.IsUsed)))
                .OrderBy(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public Task LinkAsync(DefaultIdType fileEntryId, string entityName, string entityId, string? notes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        return RunAsync(async db =>
        {
            var record = await db.Set<FileEntryRecord>().FirstOrDefaultAsync(r => r.Id == fileEntryId, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return;
            ApplyLink(record, entityName, entityId, notes);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    public Task UnlinkAsync(DefaultIdType fileEntryId, string entityName, string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        return RunAsync(async db =>
        {
            var record = await db.Set<FileEntryRecord>().FirstOrDefaultAsync(r => r.Id == fileEntryId, cancellationToken).ConfigureAwait(false);
            if (record is null || !ApplyUnlink(record, entityName, entityId))
                return;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    public Task MarkDeletedAsync(DefaultIdType id, CancellationToken cancellationToken = default)
        => RunAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            // The original and the variants generated from it.
            var records = await db.Set<FileEntryRecord>()
                .Where(r => (r.Id == id || r.ParentId == id) && !r.IsDeleted)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (records.Count == 0)
                return;
            foreach (var record in records)
            {
                record.IsDeleted = true;
                record.DeletedTime = now;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        });

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(DateTimeOffset uploadedBefore, int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return QueryAsync<IReadOnlyList<IFileEntry>>(async db =>
            await db.Set<FileEntryRecord>().AsNoTracking()
                .Where(r => !r.IsDeleted && r.ParentId == null && r.SupersededById == null && r.UploadedTime < uploadedBefore
                            && !r.RelatedEntities!.Any(l => l.IsUsed))
                .OrderBy(r => r.UploadedTime)
                .Take(maxCount)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(
        DateTimeOffset uploadedBefore, int maxCount, string? storageName, bool includeSupersededVersions = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return QueryAsync<IReadOnlyList<IFileEntry>>(async db =>
        {
            var query = ByStorage(db.Set<FileEntryRecord>().AsNoTracking(), storageName)
                .Where(r => !r.IsDeleted && r.ParentId == null && r.UploadedTime < uploadedBefore && !r.RelatedEntities!.Any(l => l.IsUsed));
            if (!includeSupersededVersions)
                query = query.Where(r => r.SupersededById == null);
            return await query.OrderBy(r => r.UploadedTime).Take(maxCount).ToListAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> FindManyAsync(IEnumerable<DefaultIdType> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Distinct().ToList();
        return QueryAsync<IReadOnlyList<IFileEntry>>(async db =>
            list.Count == 0
                ? []
                : await db.Set<FileEntryRecord>().AsNoTracking().Where(r => list.Contains(r.Id)).ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public Task<IFileEntry?> FindByLocationAsync(string fileLocation, string? storageName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileLocation);
        return QueryAsync<IFileEntry?>(async db =>
            await ByStorage(db.Set<FileEntryRecord>().AsNoTracking(), storageName)
                .FirstOrDefaultAsync(r => r.FileLocation == fileLocation && !r.IsDeleted, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public Task<FileEntryPage> QueryAsync(FileEntryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return QueryAsync(async db =>
        {
            var filtered = Filter(db.Set<FileEntryRecord>().AsNoTracking(), query);
            var total = await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var sorted = (query.SortBy, query.Descending) switch
            {
                (FileEntrySortField.Size, false) => filtered.OrderBy(r => r.Size).ThenBy(r => r.Id),
                (FileEntrySortField.Size, true) => filtered.OrderByDescending(r => r.Size).ThenBy(r => r.Id),
                (FileEntrySortField.FileName, false) => filtered.OrderBy(r => r.FileName).ThenBy(r => r.Id),
                (FileEntrySortField.FileName, true) => filtered.OrderByDescending(r => r.FileName).ThenBy(r => r.Id),
                (_, false) => filtered.OrderBy(r => r.UploadedTime).ThenBy(r => r.Id),
                _ => filtered.OrderByDescending(r => r.UploadedTime).ThenBy(r => r.Id),
            };
            var items = await sorted.Skip(query.Skip).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new FileEntryPage { Items = items, TotalCount = total, Skip = query.Skip, Take = query.Take };
        });
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FileUsage>> GetUsageAsync(FileUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return QueryAsync<IReadOnlyList<FileUsage>>(async db =>
        {
            var source = ByStorage(db.Set<FileEntryRecord>().AsNoTracking(), query.StorageName);
            if (!query.IncludeDeleted)
                source = source.Where(r => !r.IsDeleted);
            if (query.Category is { } category)
                source = source.Where(r => r.Category == category);

            // GROUP BY in the database; null instance names are pre-0.3 rows of the default instance.
            var byStorage = query.GroupBy.HasFlag(FileUsageGrouping.StorageName);
            var byCategory = query.GroupBy.HasFlag(FileUsageGrouping.Category);
            if (byStorage && byCategory)
            {
                return (await source.GroupBy(r => new { S = r.StorageName ?? StorageInstance.Default, r.Category })
                        .Select(g => new { g.Key.S, g.Key.Category, Count = g.LongCount(), Bytes = g.Sum(r => r.Size) })
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(g => new FileUsage(g.S, g.Category, g.Count, g.Bytes)).OrderBy(u => u.StorageName).ThenBy(u => u.Category).ToList();
            }

            if (byStorage)
            {
                return (await source.GroupBy(r => r.StorageName ?? StorageInstance.Default)
                        .Select(g => new { g.Key, Count = g.LongCount(), Bytes = g.Sum(r => r.Size) })
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(g => new FileUsage(g.Key, null, g.Count, g.Bytes)).OrderBy(u => u.StorageName).ToList();
            }

            if (byCategory)
            {
                return (await source.GroupBy(r => r.Category)
                        .Select(g => new { g.Key, Count = g.LongCount(), Bytes = g.Sum(r => r.Size) })
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(g => new FileUsage(null, g.Key, g.Count, g.Bytes)).OrderBy(u => u.Category).ToList();
            }

            var count = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var bytes = count == 0 ? 0 : await source.SumAsync(r => r.Size, cancellationToken).ConfigureAwait(false);
            return [new FileUsage(null, null, count, bytes)];
        });
    }

    /// <inheritdoc/>
    public Task<FileDuplicatePage> FindDuplicatesAsync(FileDuplicateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MinCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Take);
        return QueryAsync(async db =>
        {
            var source = ByStorage(db.Set<FileEntryRecord>().AsNoTracking(), query.StorageName)
                .Where(r => !r.IsDeleted && r.ParentId == null && r.Checksum != null);
            if (query.Category is { } category)
                source = source.Where(r => r.Category == category);

            var minCount = query.MinCount;
            var groups = query.AcrossStorageInstances
                ? source.GroupBy(r => new { r.Checksum, S = (string?)null })
                    .Where(g => g.Count() >= minCount)
                    .Select(g => new { g.Key.Checksum, g.Key.S, Count = g.Count(), Bytes = g.Sum(r => r.Size) })
                : source.GroupBy(r => new { r.Checksum, S = (string?)(r.StorageName ?? StorageInstance.Default) })
                    .Where(g => g.Count() >= minCount)
                    .Select(g => new { g.Key.Checksum, g.Key.S, Count = g.Count(), Bytes = g.Sum(r => r.Size) });

            var total = await groups.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var page = await groups.OrderByDescending(g => g.Count).ThenBy(g => g.Checksum).Skip(query.Skip).Take(query.Take)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var checksums = page.Select(g => g.Checksum).ToList();
            var members = await source.Where(r => checksums.Contains(r.Checksum))
                .Select(r => new { r.Id, r.Checksum, r.StorageName, r.UploadedTime })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            return new FileDuplicatePage
            {
                TotalGroups = total,
                Groups = page.Select(g => new FileDuplicateGroup(
                    g.Checksum!, g.S, g.Count, g.Bytes,
                    members.Where(m => m.Checksum == g.Checksum && (g.S is null || StorageInstance.AreSame(m.StorageName, g.S)))
                        .OrderBy(m => m.UploadedTime).ThenBy(m => m.Id).Select(m => m.Id).ToList())).ToList(),
            };
        });
    }

    /// <inheritdoc/>
    public Task RelocateAsync(IReadOnlyCollection<FileRelocation> relocations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relocations);
        var ids = relocations.Select(r => r.Id).ToList();
        return RunAsync(async db =>
        {
            var records = await db.Set<FileEntryRecord>().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var relocation in relocations)
            {
                var record = records.FirstOrDefault(r => r.Id == relocation.Id);
                if (record is null)
                    continue;
                record.StorageName = relocation.StorageName;
                record.FileLocation = relocation.FileLocation;
                record.StoragePath = relocation.StoragePath;
                record.Encrypted = relocation.Encrypted;
                if (relocation.StorageProvider is not null)
                    record.StorageProvider = relocation.StorageProvider;
                if (relocation.Metadata is not null)
                    record.Metadata = new Dictionary<string, string>(relocation.Metadata);
            }

            // One SaveChanges: all entries move together or none does.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    /// <inheritdoc/>
    public Task SupersedeAsync(DefaultIdType previousId, DefaultIdType newId, CancellationToken cancellationToken = default)
        => RunAsync(async db =>
        {
            var records = await db.Set<FileEntryRecord>().Where(r => r.Id == previousId || r.Id == newId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var previous = records.FirstOrDefault(r => r.Id == previousId)
                ?? throw new KitStack.Storage.Abstractions.Exceptions.StorageNotFoundException($"File entry '{previousId}' was not found.");
            var current = records.FirstOrDefault(r => r.Id == newId)
                ?? throw new KitStack.Storage.Abstractions.Exceptions.StorageNotFoundException($"File entry '{newId}' was not found.");
            ApplySupersede(previous, current);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        });

    internal static void ApplySupersede(FileEntryRecord previous, FileEntryRecord current)
    {
        previous.SupersededById = current.Id;
        current.PreviousVersionId = previous.Id;
        current.VersionNumber = previous.VersionNumber + 1;
        foreach (var link in previous.RelatedEntities?.Where(l => l.IsUsed).ToList() ?? [])
        {
            ApplyLink(current, link.EntityName, link.EntityId, link.Notes);
            previous.RelatedEntities!.Remove(link);
        }
    }

    private static IQueryable<FileEntryRecord> ByStorage(IQueryable<FileEntryRecord> query, string? storageName)
    {
        if (storageName is null)
            return query;
        if (StorageInstance.IsDefault(storageName))
            return query.Where(r => r.StorageName == null || r.StorageName == StorageInstance.Default);
        var name = storageName.Trim();
        return query.Where(r => r.StorageName == name);
    }

    private static IQueryable<FileEntryRecord> Filter(IQueryable<FileEntryRecord> source, FileEntryQuery query)
    {
        var q = ByStorage(source, query.StorageName);
        if (!query.IncludeDeleted)
            q = q.Where(r => !r.IsDeleted);
        if (query.Category is { } category)
            q = q.Where(r => r.Category == category);
        if (query.EntityName is not null || query.EntityId is not null)
        {
            var entityName = query.EntityName;
            var entityId = query.EntityId;
            q = q.Where(r => r.RelatedEntities!.Any(l => l.IsUsed && (entityName == null || l.EntityName == entityName) && (entityId == null || l.EntityId == entityId)));
        }

        if (!string.IsNullOrEmpty(query.ContentTypePrefix))
        {
            var prefix = query.ContentTypePrefix;
            q = q.Where(r => r.ContentType.StartsWith(prefix));
        }

        if (query.UploadedFrom is { } from)
            q = q.Where(r => r.UploadedTime >= from);
        if (query.UploadedTo is { } to)
            q = q.Where(r => r.UploadedTime < to);
        if (query.IsOrphan == true)
            q = q.Where(r => r.ParentId == null && r.SupersededById == null && !r.RelatedEntities!.Any(l => l.IsUsed));
        else if (query.IsOrphan == false)
            q = q.Where(r => r.RelatedEntities!.Any(l => l.IsUsed));
        if (query.Checksum is { } checksum)
            q = q.Where(r => r.Checksum == checksum);
        if (query.OriginalsOnly)
            q = q.Where(r => r.ParentId == null);
        if (query.OriginalId is { } originalId)
            q = q.Where(r => r.ParentId == originalId);
        if (!query.IncludeSupersededVersions)
            q = q.Where(r => r.SupersededById == null);
        return q;
    }

    internal static void ApplyLink(FileEntryRecord record, string entityName, string entityId, string? notes)
    {
        record.RelatedEntities ??= [];
        var link = record.RelatedEntities.FirstOrDefault(l => l.EntityName == entityName && l.EntityId == entityId);
        if (link is null)
        {
            record.RelatedEntities.Add(new FileRelatedEntity
            {
                EntityName = entityName,
                EntityId = entityId,
                IsUsed = true,
                Notes = notes,
                LinkedOn = DateTimeOffset.UtcNow,
            });
        }
        else if (!link.IsUsed)
        {
            link.IsUsed = true;
            link.LinkedOn = DateTimeOffset.UtcNow;
        }
    }

    internal static bool ApplyUnlink(FileEntryRecord record, string entityName, string entityId)
    {
        var link = record.RelatedEntities?.FirstOrDefault(l => l.EntityName == entityName && l.EntityId == entityId && l.IsUsed);
        if (link is null)
            return false;
        link.IsUsed = false;
        return true;
    }

    private async Task RunAsync(Func<TContext, Task> operation)
        => await QueryAsync(async db =>
        {
            await operation(db).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    private async Task<T> QueryAsync<T>(Func<TContext, Task<T>> operation)
    {
        if (_factory is not null)
        {
            var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
                return await operation(db).ConfigureAwait(false);
        }

        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
            return await operation(scope.ServiceProvider.GetRequiredService<TContext>()).ConfigureAwait(false);
    }
}
