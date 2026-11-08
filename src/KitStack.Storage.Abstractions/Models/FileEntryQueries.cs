using KitStack.Storage.Abstractions.Interfaces;

namespace KitStack.Storage.Abstractions.Models;

/// <summary>Filters, sort and paging for <c>IFileEntryStore.QueryAsync</c>. All filters are optional and combined with AND.</summary>
public sealed class FileEntryQuery
{
    /// <summary>Storage instance; <c>default</c> also matches entries without a name (pre-0.3 data).</summary>
    public string? StorageName { get; set; }

    /// <summary>Exact category.</summary>
    public string? Category { get; set; }

    /// <summary>Linked entity name (active links only).</summary>
    public string? EntityName { get; set; }

    /// <summary>Linked entity id (active links only); combined with <see cref="EntityName"/> when both are set.</summary>
    public string? EntityId { get; set; }

    /// <summary>Content type prefix, for example <c>image/</c> or <c>application/pdf</c>.</summary>
    public string? ContentTypePrefix { get; set; }

    /// <summary>Uploaded at or after.</summary>
    public DateTimeOffset? UploadedFrom { get; set; }

    /// <summary>Uploaded before.</summary>
    public DateTimeOffset? UploadedTo { get; set; }

    /// <summary>Also return deleted entries.</summary>
    public bool IncludeDeleted { get; set; }

    /// <summary>
    /// <c>true</c>: only orphans (originals that are current versions and have no active link); <c>false</c>: only entries
    /// with an active link; <c>null</c>: both.
    /// </summary>
    public bool? IsOrphan { get; set; }

    /// <summary>Exact checksum.</summary>
    public string? Checksum { get; set; }

    /// <summary>Exclude image variants.</summary>
    public bool OriginalsOnly { get; set; }

    /// <summary>Only the variants generated from this original.</summary>
    public DefaultIdType? OriginalId { get; set; }

    /// <summary>Also return previous versions kept by <c>ReplaceOptions.KeepPreviousVersion</c>. Default <c>true</c>.</summary>
    public bool IncludeSupersededVersions { get; set; } = true;

    /// <summary>Sort field.</summary>
    public FileEntrySortField SortBy { get; set; } = FileEntrySortField.UploadedTime;

    /// <summary>Sort descending (default).</summary>
    public bool Descending { get; set; } = true;

    /// <summary>Entries to skip.</summary>
    public int Skip { get; set; }

    /// <summary>Maximum entries to return (1-1000). Default 50.</summary>
    public int Take { get; set; } = 50;

    /// <summary>Throw when paging values are out of range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Take);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Take, 1000);
    }
}

/// <summary>Sort fields of <see cref="FileEntryQuery"/>.</summary>
public enum FileEntrySortField
{
    /// <summary>Upload time.</summary>
    UploadedTime,

    /// <summary>Size in bytes.</summary>
    Size,

    /// <summary>File name.</summary>
    FileName,
}

/// <summary>A page of <see cref="FileEntryQuery"/> results.</summary>
public sealed class FileEntryPage
{
    /// <summary>The entries of this page.</summary>
    public IReadOnlyList<IFileEntry> Items { get; init; } = [];

    /// <summary>Total matching entries (all pages).</summary>
    public long TotalCount { get; init; }

    /// <summary>Entries skipped.</summary>
    public int Skip { get; init; }

    /// <summary>Page size requested.</summary>
    public int Take { get; init; }
}

/// <summary>Dimensions of <c>IFileEntryStore.GetUsageAsync</c>.</summary>
[Flags]
public enum FileUsageGrouping
{
    /// <summary>One total row.</summary>
    None = 0,

    /// <summary>Group by storage instance (<c>null</c> names reported as <c>default</c>).</summary>
    StorageName = 1,

    /// <summary>Group by category.</summary>
    Category = 2,
}

/// <summary>Arguments of <c>IFileEntryStore.GetUsageAsync</c>. Originals, variants and kept previous versions are all counted.</summary>
public sealed class FileUsageQuery
{
    /// <summary>Grouping dimensions. Default by storage instance.</summary>
    public FileUsageGrouping GroupBy { get; set; } = FileUsageGrouping.StorageName;

    /// <summary>Only this instance.</summary>
    public string? StorageName { get; set; }

    /// <summary>Only this category.</summary>
    public string? Category { get; set; }

    /// <summary>Also count deleted entries.</summary>
    public bool IncludeDeleted { get; set; }
}

/// <summary>One row of <c>IFileEntryStore.GetUsageAsync</c>; dimensions that are not grouped are <c>null</c>.</summary>
public sealed record FileUsage(string? StorageName, string? Category, long FileCount, long TotalBytes);

/// <summary>Arguments of <c>IFileEntryStore.FindDuplicatesAsync</c>.</summary>
public sealed class FileDuplicateQuery
{
    /// <summary>Only this instance.</summary>
    public string? StorageName { get; set; }

    /// <summary>Only this category.</summary>
    public string? Category { get; set; }

    /// <summary>Minimum entries sharing a checksum. Default 2.</summary>
    public int MinCount { get; set; } = 2;

    /// <summary>
    /// Group identical content across instances. Off by default: each group belongs to one instance, since deduplicating
    /// across instances with different policies or encryption is rarely wanted.
    /// </summary>
    public bool AcrossStorageInstances { get; set; }

    /// <summary>Groups to skip.</summary>
    public int Skip { get; set; }

    /// <summary>Maximum groups to return. Default 50.</summary>
    public int Take { get; set; } = 50;
}

/// <summary>Non-deleted originals with identical content.</summary>
/// <param name="Checksum">Shared SHA-256.</param>
/// <param name="StorageName">Instance of the group, or <c>null</c> for cross-instance groups.</param>
/// <param name="Count">Number of entries.</param>
/// <param name="TotalBytes">Combined size.</param>
/// <param name="EntryIds">Ids of the entries, oldest first.</param>
public sealed record FileDuplicateGroup(string Checksum, string? StorageName, int Count, long TotalBytes, IReadOnlyList<DefaultIdType> EntryIds);

/// <summary>A page of duplicate groups, largest groups first.</summary>
public sealed class FileDuplicatePage
{
    /// <summary>Groups of this page.</summary>
    public IReadOnlyList<FileDuplicateGroup> Groups { get; init; } = [];

    /// <summary>Total number of groups.</summary>
    public long TotalGroups { get; init; }
}

/// <summary>New location of an entry after its bytes moved to another instance.</summary>
/// <param name="Id">Entry id (kept).</param>
/// <param name="StorageName">Target instance.</param>
/// <param name="FileLocation">New provider location.</param>
/// <param name="StoragePath">New storage path.</param>
/// <param name="Encrypted">Whether the new bytes are encrypted.</param>
/// <param name="StorageProvider">New provider identifier, or <c>null</c> to keep it.</param>
/// <param name="Metadata">Replacement metadata (variant paths), or <c>null</c> to keep it.</param>
public sealed record FileRelocation(
    DefaultIdType Id, string StorageName, string FileLocation, string? StoragePath, bool Encrypted,
    string? StorageProvider = null, IDictionary<string, string>? Metadata = null);
