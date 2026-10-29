using KitStack.Storage.EntityFrameworkCore.Configuration;
using KitStack.Storage.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;

namespace KitStack.Storage.EntityFrameworkCore;

/// <summary>
/// Stand-alone context holding only the file metadata tables, for applications that keep file metadata in
/// its own database (the role the ERP's StorageContext played). Applications that want the tables in their
/// own context call <c>modelBuilder.ApplyKitStackStorage(this)</c> instead.
/// </summary>
public class KitStackStorageDbContext(DbContextOptions<KitStackStorageDbContext> options) : DbContext(options)
{
    public DbSet<FileEntryRecord> FileEntries => Set<FileEntryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyKitStackStorage(this);
    }
}
