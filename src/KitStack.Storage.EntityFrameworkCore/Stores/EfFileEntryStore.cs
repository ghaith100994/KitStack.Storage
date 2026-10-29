using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
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
                .Where(r => !r.IsDeleted && r.ParentId == null && r.UploadedTime < uploadedBefore
                            && !r.RelatedEntities!.Any(l => l.IsUsed))
                .OrderBy(r => r.UploadedTime)
                .Take(maxCount)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
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
