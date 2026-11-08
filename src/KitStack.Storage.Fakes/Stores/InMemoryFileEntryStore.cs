using KitStack.Storage.Abstractions.Exceptions;
using KitStack.Storage.Abstractions.Instances;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Abstractions.Models;

namespace KitStack.Storage.Fakes.Stores;

/// <summary>
/// Thread-safe in-memory <see cref="IFileEntryStore"/> with the same semantics as the EF Core and MongoDB stores
/// (links, orphans, queries, usage, duplicates, relocation, versions). Entries are copied in and out, like a database.
/// Register it with <c>AddInMemoryStore()</c>.
/// </summary>
public sealed class InMemoryFileEntryStore : IFileEntryStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DefaultIdType, FileEntry> _entries = [];

    /// <summary>Snapshot of every stored entry (including deleted ones), for assertions.</summary>
    public IReadOnlyList<IFileEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.Values.Select(Copy).ToList();
        }
    }

    /// <summary>Remove everything.</summary>
    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    /// <inheritdoc/>
    public Task AddAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
            _entries.TryAdd(entry.Id, Copy(entry));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateAsync(IFileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Id, out var stored))
            {
                stored.Size = entry.Size;
                stored.Checksum = entry.Checksum;
                stored.ContentType = entry.ContentType;
                stored.Encrypted = entry.Encrypted;
                stored.LastAccessedTime = entry.LastAccessedTime;
                stored.Metadata = entry.Metadata is null ? null : new Dictionary<string, string>(entry.Metadata);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IFileEntry?> FindAsync(DefaultIdType id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IFileEntry?>(_entries.TryGetValue(id, out var e) ? Copy(e) : null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> FindManyAsync(IEnumerable<DefaultIdType> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<IFileEntry>>(ids.Distinct().Where(_entries.ContainsKey).Select(id => (IFileEntry)Copy(_entries[id])).ToList());
    }

    /// <inheritdoc/>
    public Task<IFileEntry?> FindByLocationAsync(string fileLocation, CancellationToken cancellationToken = default)
        => FindByLocationAsync(fileLocation, null, cancellationToken);

    /// <inheritdoc/>
    public Task<IFileEntry?> FindByLocationAsync(string fileLocation, string? storageName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileLocation);
        lock (_gate)
            return Task.FromResult<IFileEntry?>(_entries.Values
                .Where(e => e.FileLocation == fileLocation && !e.IsDeleted && InStorage(e, storageName))
                .Select(Copy).FirstOrDefault());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> ListByEntityAsync(string entityName, string entityId, bool includeUnused = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<IFileEntry>>(_entries.Values
                .Where(e => !e.IsDeleted && (e.RelatedEntities ?? []).Any(l => l.EntityName == entityName && l.EntityId == entityId && (includeUnused || l.IsUsed)))
                .OrderBy(e => e.UploadedTime).Select(e => (IFileEntry)Copy(e)).ToList());
    }

    /// <inheritdoc/>
    public Task LinkAsync(DefaultIdType fileEntryId, string entityName, string entityId, string? notes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        lock (_gate)
        {
            if (_entries.TryGetValue(fileEntryId, out var entry))
                Link(entry, entityName, entityId, notes);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UnlinkAsync(DefaultIdType fileEntryId, string entityName, string entityId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(fileEntryId, out var entry)
                && entry.RelatedEntities?.FirstOrDefault(l => l.EntityName == entityName && l.EntityId == entityId && l.IsUsed) is { } link)
                link.IsUsed = false;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MarkDeletedAsync(DefaultIdType id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values.Where(e => (e.Id == id || ParentOf(e) == id) && !e.IsDeleted))
                entry.IsDeleted = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(DateTimeOffset uploadedBefore, int maxCount, CancellationToken cancellationToken = default)
        => ListOrphansAsync(uploadedBefore, maxCount, null, false, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<IFileEntry>> ListOrphansAsync(
        DateTimeOffset uploadedBefore, int maxCount, string? storageName, bool includeSupersededVersions = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<IFileEntry>>(_entries.Values
                .Where(e => !e.IsDeleted && ParentOf(e) is null && e.UploadedTime < uploadedBefore && !IsUsed(e) && InStorage(e, storageName)
                            && (includeSupersededVersions || e.SupersededById is null))
                .OrderBy(e => e.UploadedTime).Take(maxCount).Select(e => (IFileEntry)Copy(e)).ToList());
    }

    /// <inheritdoc/>
    public Task<FileEntryPage> QueryAsync(FileEntryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        lock (_gate)
        {
            var filtered = _entries.Values.Where(e => Matches(e, query)).ToList();
            IEnumerable<FileEntry> sorted = query.SortBy switch
            {
                FileEntrySortField.Size => query.Descending ? filtered.OrderByDescending(e => e.Size) : filtered.OrderBy(e => e.Size),
                FileEntrySortField.FileName => query.Descending ? filtered.OrderByDescending(e => e.FileName, StringComparer.Ordinal) : filtered.OrderBy(e => e.FileName, StringComparer.Ordinal),
                _ => query.Descending ? filtered.OrderByDescending(e => e.UploadedTime) : filtered.OrderBy(e => e.UploadedTime),
            };
            return Task.FromResult(new FileEntryPage
            {
                Items = sorted.Skip(query.Skip).Take(query.Take).Select(e => (IFileEntry)Copy(e)).ToList(),
                TotalCount = filtered.Count,
                Skip = query.Skip,
                Take = query.Take,
            });
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FileUsage>> GetUsageAsync(FileUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var byStorage = query.GroupBy.HasFlag(FileUsageGrouping.StorageName);
        var byCategory = query.GroupBy.HasFlag(FileUsageGrouping.Category);
        lock (_gate)
        {
            var rows = _entries.Values
                .Where(e => (query.IncludeDeleted || !e.IsDeleted) && InStorage(e, query.StorageName) && (query.Category is null || e.Category == query.Category))
                .GroupBy(e => (S: byStorage ? StorageInstance.Normalize(e.StorageName) : null, C: byCategory ? e.Category : null))
                .Select(g => new FileUsage(g.Key.S, g.Key.C, g.LongCount(), g.Sum(e => e.Size)))
                .OrderBy(u => u.StorageName).ThenBy(u => u.Category)
                .ToList();
            if (rows.Count == 0 && query.GroupBy == FileUsageGrouping.None)
                rows.Add(new FileUsage(null, null, 0, 0));
            return Task.FromResult<IReadOnlyList<FileUsage>>(rows);
        }
    }

    /// <inheritdoc/>
    public Task<FileDuplicatePage> FindDuplicatesAsync(FileDuplicateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MinCount, 2);
        lock (_gate)
        {
            var groups = _entries.Values
                .Where(e => !e.IsDeleted && ParentOf(e) is null && e.Checksum is not null && InStorage(e, query.StorageName)
                            && (query.Category is null || e.Category == query.Category))
                .GroupBy(e => (Checksum: e.Checksum!, S: query.AcrossStorageInstances ? null : StorageInstance.Normalize(e.StorageName)))
                .Where(g => g.Count() >= query.MinCount)
                .Select(g => new FileDuplicateGroup(g.Key.Checksum, g.Key.S, g.Count(), g.Sum(e => e.Size),
                    g.OrderBy(e => e.UploadedTime).ThenBy(e => e.Id).Select(e => e.Id).ToList()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Checksum, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(new FileDuplicatePage { TotalGroups = groups.Count, Groups = groups.Skip(query.Skip).Take(query.Take).ToList() });
        }
    }

    /// <inheritdoc/>
    public Task RelocateAsync(IReadOnlyCollection<FileRelocation> relocations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relocations);
        lock (_gate)
        {
            foreach (var relocation in relocations)
            {
                if (!_entries.TryGetValue(relocation.Id, out var entry))
                    continue;
                entry.StorageName = relocation.StorageName;
                entry.FileLocation = relocation.FileLocation;
                entry.StoragePath = relocation.StoragePath;
                entry.Encrypted = relocation.Encrypted;
                if (relocation.StorageProvider is not null)
                    entry.StorageProvider = relocation.StorageProvider;
                if (relocation.Metadata is not null)
                    entry.Metadata = new Dictionary<string, string>(relocation.Metadata);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SupersedeAsync(DefaultIdType previousId, DefaultIdType newId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(previousId, out var previous))
                throw new StorageNotFoundException($"File entry '{previousId}' was not found.");
            if (!_entries.TryGetValue(newId, out var current))
                throw new StorageNotFoundException($"File entry '{newId}' was not found.");

            previous.SupersededById = newId;
            current.PreviousVersionId = previousId;
            current.VersionNumber = previous.VersionNumber + 1;
            foreach (var link in previous.RelatedEntities?.Where(l => l.IsUsed).ToList() ?? [])
            {
                Link(current, link.EntityName, link.EntityId, link.Notes);
                previous.RelatedEntities!.Remove(link);
            }
        }

        return Task.CompletedTask;
    }

    private static bool Matches(FileEntry e, FileEntryQuery q)
    {
        if (!q.IncludeDeleted && e.IsDeleted)
            return false;
        if (!InStorage(e, q.StorageName))
            return false;
        if (q.Category is not null && e.Category != q.Category)
            return false;
        if ((q.EntityName is not null || q.EntityId is not null)
            && !(e.RelatedEntities ?? []).Any(l => l.IsUsed && (q.EntityName is null || l.EntityName == q.EntityName) && (q.EntityId is null || l.EntityId == q.EntityId)))
            return false;
        if (!string.IsNullOrEmpty(q.ContentTypePrefix) && !e.ContentType.StartsWith(q.ContentTypePrefix, StringComparison.Ordinal))
            return false;
        if (q.UploadedFrom is { } from && e.UploadedTime < from)
            return false;
        if (q.UploadedTo is { } to && e.UploadedTime >= to)
            return false;
        if (q.IsOrphan == true && (ParentOf(e) is not null || e.SupersededById is not null || IsUsed(e)))
            return false;
        if (q.IsOrphan == false && !IsUsed(e))
            return false;
        if (q.Checksum is not null && e.Checksum != q.Checksum)
            return false;
        if (q.OriginalsOnly && ParentOf(e) is not null)
            return false;
        if (q.OriginalId is { } originalId && ParentOf(e) != originalId)
            return false;
        return q.IncludeSupersededVersions || e.SupersededById is null;
    }

    private static bool InStorage(IFileEntry entry, string? storageName)
        => storageName is null || StorageInstance.AreSame(entry.StorageName, storageName);

    private static bool IsUsed(IFileEntry entry) => (entry.RelatedEntities ?? []).Any(l => l.IsUsed);

    private static DefaultIdType? ParentOf(IFileEntry entry)
        => entry.Metadata is { } md && md.TryGetValue(FileEntryMetadataKeys.OriginalId, out var raw) && Guid.TryParse(raw, out var id) ? id : null;

    private static void Link(FileEntry entry, string entityName, string entityId, string? notes)
    {
        entry.RelatedEntities ??= [];
        var link = entry.RelatedEntities.FirstOrDefault(l => l.EntityName == entityName && l.EntityId == entityId);
        if (link is null)
        {
            entry.RelatedEntities.Add(new FileRelatedEntity { EntityName = entityName, EntityId = entityId, Notes = notes, IsUsed = true, LinkedOn = DateTimeOffset.UtcNow });
        }
        else if (!link.IsUsed)
        {
            link.IsUsed = true;
            link.LinkedOn = DateTimeOffset.UtcNow;
        }
    }

    private static FileEntry Copy(IFileEntry e) => new()
    {
        Id = e.Id,
        FileName = e.FileName,
        FileLocation = e.FileLocation,
        StoragePath = e.StoragePath,
        Category = e.Category,
        Size = e.Size,
        ContentType = e.ContentType,
        Checksum = e.Checksum,
        FileExtension = e.FileExtension,
        Metadata = e.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(e.Metadata),
        UploadedTime = e.UploadedTime,
        VariantType = e.VariantType,
        Encrypted = e.Encrypted,
        OriginalFileName = e.OriginalFileName,
        StorageProvider = e.StorageProvider,
        LastAccessedTime = e.LastAccessedTime,
        IsDeleted = e.IsDeleted,
        StorageName = e.StorageName,
        PreviousVersionId = e.PreviousVersionId,
        VersionNumber = e.VersionNumber < 1 ? 1 : e.VersionNumber,
        SupersededById = e.SupersededById,
        RelatedEntities = (e.RelatedEntities ?? []).Select(l => new FileRelatedEntity
        {
            EntityName = l.EntityName,
            EntityId = l.EntityId,
            IsUsed = l.IsUsed,
            Notes = l.Notes,
            LinkedOn = l.LinkedOn,
        }).ToList(),
    };
}
