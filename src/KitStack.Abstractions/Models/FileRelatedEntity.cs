using System;

namespace KitStack.Abstractions.Models;

/// <summary>
/// Describes a relationship between a file entry and another entity in the system.
/// </summary>
public class FileRelatedEntity
{
    /// <summary>
    /// Identifier of the related entity.
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly name or logical folder for the entity.
    /// </summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Flag indicating whether this file is actively used by the entity.
    /// </summary>
    public bool IsUsed { get; set; } = true;

    /// <summary>
    /// Optional notes to describe the relationship (for example "primary avatar").
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When the relationship was recorded.
    /// </summary>
    public DateTimeOffset LinkedOn { get; set; } = DateTimeOffset.UtcNow;
}
