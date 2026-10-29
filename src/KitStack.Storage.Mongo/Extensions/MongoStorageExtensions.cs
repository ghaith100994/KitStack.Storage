using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace KitStack.Storage.Mongo.Extensions;

/// <summary>Options for <see cref="MongoStorageExtensions.AddMongoStore(KitStackStorageBuilder, Action{MongoFileEntryStoreOptions}?, bool)"/>.</summary>
public sealed class MongoFileEntryStoreOptions
{
    /// <summary>MongoDB connection string. Defaults to <c>Storage:Database:ConnectionString</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Database name. Defaults to <c>Storage:Database:DatabaseName</c>.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Collection name. Default <c>fileEntries</c>.</summary>
    public string CollectionName { get; set; } = MongoFileEntryStore.DefaultCollectionName;
}

/// <summary>Registration helpers for the MongoDB metadata store.</summary>
public static class MongoStorageExtensions
{
    /// <summary>
    /// Use MongoDB as the <see cref="IFileEntryStore"/>, connecting with <c>Storage:Database</c>
    /// (<c>ConnectionString</c>, <c>DatabaseName</c>, optional <c>CollectionName</c>) unless configured in code.
    /// </summary>
    /// <param name="builder">The storage builder; add the provider first.</param>
    /// <param name="configure">Optional code configuration, applied after the configuration section.</param>
    /// <param name="trackUploads">Record every file created through <see cref="IFileStorageManager"/> (default).</param>
    public static KitStackStorageBuilder AddMongoStore(
        this KitStackStorageBuilder builder, Action<MongoFileEntryStoreOptions>? configure = null, bool trackUploads = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new MongoFileEntryStoreOptions();
        builder.Configuration.GetSection("Database").Bind(options);
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString) || string.IsNullOrWhiteSpace(options.DatabaseName))
            throw new InvalidOperationException("Configure Storage:Database:ConnectionString and Storage:Database:DatabaseName for the Mongo file store.");

        return builder.AddMongoStore(
            _ => new MongoClient(options.ConnectionString).GetDatabase(options.DatabaseName),
            options.CollectionName,
            trackUploads);
    }

    /// <summary>
    /// Use MongoDB as the <see cref="IFileEntryStore"/> with a database supplied by the application
    /// (for example one built from a shared <see cref="IMongoClient"/>).
    /// </summary>
    public static KitStackStorageBuilder AddMongoStore(
        this KitStackStorageBuilder builder, Func<IServiceProvider, IMongoDatabase> database,
        string collectionName = MongoFileEntryStore.DefaultCollectionName, bool trackUploads = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        builder.Services.TryAddSingleton(sp => new MongoFileEntryStore(database(sp), collectionName));
        builder.Services.TryAddSingleton<IFileEntryStore>(sp => sp.GetRequiredService<MongoFileEntryStore>());
        builder.Services.TryAddScoped<FileOrphanSweeper>();
        if (trackUploads)
            builder.AddFileEntryTracking();
        return builder;
    }
}
