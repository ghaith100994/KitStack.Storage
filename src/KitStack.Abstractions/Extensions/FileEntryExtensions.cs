using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;

namespace KitStack.Abstractions.Extensions;

/// <summary>
/// Helper extensions for working with <see cref="IFileEntry"/>.
/// </summary>
public static class FileEntryExtensions
{
    /// <summary>
    /// Link the file entry to the provided entity by capturing its identifier and logical name.
    /// </summary>
    /// <param name="fileEntry">Target file entry.</param>
    /// <param name="entity">Entity to relate to.</param>
    /// <param name="entityName">Optional friendly name/folder for the entity.</param>
    /// <param name="markAsUsed">Flag to indicate whether the file is actively used by the entity.</param>
    /// <param name="notes">Optional notes stored alongside the relation.</param>
    /// <exception cref="StorageValidationException">Thrown when the entity cannot be resolved.</exception>
    public static void LinkToEntity(this IFileEntry fileEntry, object entity, string? entityName = null, bool markAsUsed = true, string? notes = null)
    {
        if (fileEntry == null)
            throw new StorageValidationException("File entry cannot be null when linking entities.");
        if (entity == null)
            throw new StorageValidationException("Entity instance must be provided to link file entries.");

        var entityId = ResolveEntityIdentifier(entity);
        var friendlyName = string.IsNullOrWhiteSpace(entityName) ? entity.GetType().Name : entityName;

        fileEntry.RelatedEntities ??= new List<FileRelatedEntity>();
        if (fileEntry.RelatedEntities.Any(r => string.Equals(r.EntityId, entityId, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(r.EntityName, friendlyName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        fileEntry.RelatedEntities.Add(new FileRelatedEntity
        {
            EntityId = entityId,
            EntityName = friendlyName,
            IsUsed = markAsUsed,
            Notes = notes,
            LinkedOn = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Copy related entity records from the source to the target entry.
    /// </summary>
    public static void CopyRelationsFrom(this IFileEntry target, IFileEntry? source)
    {
        if (target == null || source?.RelatedEntities == null || source.RelatedEntities.Count == 0)
            return;

        target.RelatedEntities ??= [];
        foreach (var relation in source.RelatedEntities)
        {
            if (target.RelatedEntities.Any(r => string.Equals(r.EntityId, relation.EntityId, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(r.EntityName, relation.EntityName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            target.RelatedEntities.Add(new FileRelatedEntity
            {
                EntityId = relation.EntityId,
                EntityName = relation.EntityName,
                IsUsed = relation.IsUsed,
                LinkedOn = relation.LinkedOn,
                Notes = relation.Notes
            });
        }
    }

    private static string ResolveEntityIdentifier(object entity)
    {
        var type = entity.GetType();
        var idProp = type.GetProperty("Id") ?? type.GetProperty($"{type.Name}Id");
        if (idProp == null)
            throw new StorageValidationException($"Entity '{type.Name}' must expose an Id or {type.Name}Id property to relate files.");

        var raw = idProp.GetValue(entity);
        if (raw == null)
            throw new StorageValidationException($"Entity '{type.Name}' Id cannot be null when relating files.");

        var idValue = Convert.ToString(raw, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(idValue))
            throw new StorageValidationException($"Entity '{type.Name}' Id cannot be empty when relating files.");

        return idValue;
    }
}
