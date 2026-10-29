using System.Globalization;
using System.Runtime.CompilerServices;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.EntityFrameworkCore.Entities;
using KitStack.Storage.EntityFrameworkCore.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KitStack.Storage.EntityFrameworkCore.Interceptors;

/// <summary>
/// Keeps file links in step with the <see cref="StoredFileRef"/> owned values of saved entities:
/// a reference that is set links the file to its owner (<c>EntityName</c> = owner CLR type name,
/// <c>EntityId</c> = owner key), and a reference that is replaced, cleared or deleted with its owner
/// releases that link, so the old file becomes an orphan the sweeper can remove.
/// </summary>
/// <remarks>
/// When the saving context also holds the file metadata tables the links are written in the same
/// transaction. Otherwise they are written through <see cref="IFileEntryStore"/> after the save succeeds.
/// Register it with <c>optionsBuilder.AddKitStackFileReferences(serviceProvider)</c>.
/// </remarks>
public sealed class FileReferenceInterceptor(IFileEntryStore store) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<LinkChange>> _afterSave = new();

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
            Prepare(context, inline => ApplyInline(context, inline, (set, id) => set.Find(id)));
        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
        {
            List<LinkChange>? inline = null;
            Prepare(context, changes => inline = changes);
            if (inline is not null)
            {
                var records = new Dictionary<DefaultIdType, FileEntryRecord?>();
                foreach (var id in inline.Select(c => c.FileEntryId).Distinct())
                    records[id] = await context.Set<FileEntryRecord>().FindAsync([id], cancellationToken).ConfigureAwait(false);
                ApplyInline(context, inline, (_, id) => records[id]);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
            ApplyAfterSaveAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
            await ApplyAfterSaveAsync(context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
            _afterSave.Remove(context);
    }

    /// <inheritdoc/>
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    private void Prepare(DbContext context, Action<List<LinkChange>> applyInline)
    {
        _afterSave.Remove(context);
        var changes = Collect(context);
        if (changes.Count == 0)
            return;

        var holdsMetadata = context.Model.FindEntityType(typeof(FileEntryRecord)) is not null;
        // Owners with store-generated keys only know their key after the save.
        var inline = holdsMetadata ? changes.Where(c => c.PendingOwner is null).ToList() : [];
        var later = changes.Except(inline).ToList();

        if (inline.Count > 0)
            applyInline(inline);
        if (later.Count > 0)
            _afterSave.AddOrUpdate(context, later);
    }

    private static List<LinkChange> Collect(DbContext context)
    {
        if (context.ChangeTracker.AutoDetectChangesEnabled)
            context.ChangeTracker.DetectChanges();

        var changes = new List<LinkChange>();
        foreach (var entry in context.ChangeTracker.Entries<StoredFileRef>())
        {
            var ownership = entry.Metadata.FindOwnership();
            if (ownership is null)
                continue;
            var ownerName = ownership.PrincipalEntityType.ClrType.Name;
            var fileId = entry.Property(r => r.FileEntryId);

            switch (entry.State)
            {
                case EntityState.Added:
                    changes.Add(Link(entry, ownerName, ownership.Properties, fileId.CurrentValue));
                    break;
                case EntityState.Deleted:
                    changes.Add(Unlink(entry, ownerName, ownership.Properties, fileId.OriginalValue));
                    break;
                case EntityState.Modified when fileId.IsModified && fileId.OriginalValue != fileId.CurrentValue:
                    changes.Add(Unlink(entry, ownerName, ownership.Properties, fileId.OriginalValue));
                    changes.Add(Link(entry, ownerName, ownership.Properties, fileId.CurrentValue));
                    break;
            }
        }

        // Release before linking, so replacing a reference with one to the same file keeps it linked.
        return [.. changes.Where(c => !c.IsLink), .. changes.Where(c => c.IsLink)];
    }

    private static LinkChange Link(EntityEntry<StoredFileRef> entry, string ownerName, IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IProperty> keys, DefaultIdType fileId)
    {
        var temporary = keys.Any(k => entry.Property(k.Name).IsTemporary);
        return new LinkChange(true, fileId, ownerName, temporary ? null : OwnerId(entry, keys, original: false),
            temporary ? (entry, keys) : null, entry.Entity);
    }

    private static LinkChange Unlink(EntityEntry<StoredFileRef> entry, string ownerName, IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IProperty> keys, DefaultIdType fileId)
        => new(false, fileId, ownerName, OwnerId(entry, keys, original: true), null, null);

    private static string OwnerId(EntityEntry entry, IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IProperty> keys, bool original)
        => string.Join(',', keys.Select(k =>
        {
            var property = entry.Property(k.Name);
            return Convert.ToString(original ? property.OriginalValue : property.CurrentValue, CultureInfo.InvariantCulture);
        }));

    private static void ApplyInline(DbContext context, List<LinkChange> changes, Func<DbSet<FileEntryRecord>, DefaultIdType, FileEntryRecord?> find)
    {
        var set = context.Set<FileEntryRecord>();
        var added = new Dictionary<DefaultIdType, FileEntryRecord>();

        foreach (var change in changes)
        {
            var record = added.GetValueOrDefault(change.FileEntryId) ?? find(set, change.FileEntryId);
            if (change.IsLink)
            {
                if (record is null && change.Snapshot is not null)
                {
                    // The file was stored without tracking: record it so it can be found and swept later.
                    record = FileEntryRecord.From(change.Snapshot.ToFileEntry());
                    set.Add(record);
                    added[record.Id] = record;
                }

                if (record is not null)
                    EfFileEntryStore<DbContext>.ApplyLink(record, change.EntityName, change.EntityId!, notes: null);
            }
            else if (record is not null)
            {
                EfFileEntryStore<DbContext>.ApplyUnlink(record, change.EntityName, change.EntityId!);
            }
        }

        context.ChangeTracker.DetectChanges();
    }

    private async Task ApplyAfterSaveAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (!_afterSave.TryGetValue(context, out var changes))
            return;
        _afterSave.Remove(context);

        foreach (var change in changes)
        {
            var entityId = change.EntityId
                ?? OwnerId(change.PendingOwner!.Value.Entry, change.PendingOwner.Value.Keys, original: false);

            if (!change.IsLink)
            {
                await store.UnlinkAsync(change.FileEntryId, change.EntityName, entityId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (change.Snapshot is not null
                && await store.FindAsync(change.FileEntryId, cancellationToken).ConfigureAwait(false) is null)
                await store.AddAsync(change.Snapshot.ToFileEntry(), cancellationToken).ConfigureAwait(false);
            await store.LinkAsync(change.FileEntryId, change.EntityName, entityId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record LinkChange(
        bool IsLink,
        DefaultIdType FileEntryId,
        string EntityName,
        string? EntityId,
        (EntityEntry Entry, IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IProperty> Keys)? PendingOwner,
        StoredFileRef? Snapshot);
}
