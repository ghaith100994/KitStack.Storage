using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Services;
using KitStack.Storage.EntityFrameworkCore.Interceptors;
using KitStack.Storage.EntityFrameworkCore.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KitStack.Storage.EntityFrameworkCore.Extensions;

/// <summary>
/// Registration helpers for the EF Core metadata store.
/// </summary>
public static class EntityFrameworkCoreStorageExtensions
{
    /// <summary>
    /// Use <typeparamref name="TContext"/> (whose model calls <c>ApplyKitStackStorage</c>) as the
    /// <see cref="IFileEntryStore"/>, and register <see cref="FileReferenceInterceptor"/> for
    /// <c>AddKitStackFileReferences</c>.
    /// </summary>
    /// <param name="builder">The storage builder; add the provider first.</param>
    /// <param name="trackUploads">
    /// Record every file created through <see cref="IFileStorageManager"/> (default). Requires a provider to be
    /// registered already; see <see cref="KitStackStorageServiceCollectionExtensions.AddFileEntryTracking"/>.
    /// </param>
    public static KitStackStorageBuilder AddEntityFrameworkCoreStore<TContext>(this KitStackStorageBuilder builder, bool trackUploads = true)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IFileEntryStore, EfFileEntryStore<TContext>>();
        builder.Services.TryAddSingleton<FileReferenceInterceptor>();
        builder.Services.TryAddScoped<FileOrphanSweeper>();
        if (trackUploads)
            builder.AddFileEntryTracking();
        return builder;
    }

    /// <summary>
    /// Keep file metadata in its own database through <see cref="KitStackStorageDbContext"/>.
    /// </summary>
    public static KitStackStorageBuilder AddKitStackStorageDbContext(
        this KitStackStorageBuilder builder, Action<DbContextOptionsBuilder> configure, bool trackUploads = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddDbContextFactory<KitStackStorageDbContext>(configure);
        return builder.AddEntityFrameworkCoreStore<KitStackStorageDbContext>(trackUploads);
    }

    /// <summary>
    /// Add <see cref="FileReferenceInterceptor"/> to a context that has <c>StoredFileRef</c> properties:
    /// <code>services.AddDbContext&lt;AppDbContext&gt;((sp, o) => o.UseSqlServer(cs).AddKitStackFileReferences(sp));</code>
    /// </summary>
    public static DbContextOptionsBuilder AddKitStackFileReferences(this DbContextOptionsBuilder options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        return options.AddInterceptors(services.GetRequiredService<FileReferenceInterceptor>());
    }
}
