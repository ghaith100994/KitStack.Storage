using System.Collections.Concurrent;
using KitStack.Abstractions.Models;
using Microsoft.AspNetCore.Http;

namespace KitStack.Abstractions.Interfaces;

/// <summary>
/// Abstraction for file storage operations.
/// Implementations should be lightweight and provider-specific.
/// </summary>
public interface IFileStorageManager
{
    /// <summary>
    /// Create and store a file for the specified entity type <typeparamref name="T"/>.
    /// The implementation should produce a populated <see cref="IFileEntry"/> describing
    /// the stored primary/original file. Providers may also create image variants according
    /// to their configuration but this method returns the primary entry only.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the stored <see cref="IFileEntry"/>.</returns>
    Task<IFileEntry> CreateAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Create and store a file associated with the provided entity instance.
    /// Implementations may attach or mutate the entity (for example adding a FileEntry)
    /// when the entity implements <see cref="IFileAttachable"/>. Implementations should also
    /// record a relationship on the returned <see cref="IFileEntry"/> via <c>RelatedEntities</c>.
    /// </summary>
    /// <typeparam name="T">Entity type which implements <see cref="IFileAttachable"/>.</typeparam>
    /// <param name="entity">The entity instance to associate the file with.</param>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the stored <see cref="IFileEntry"/>.</returns>
    Task<IFileEntry> CreateAsync<T>(T entity, IFormFile file, string? category, CancellationToken cancellationToken = default)
        where T : class, IFileAttachable;

    /// <summary>
    /// Create the primary/original file and any configured image variants (thumbnail, compressed,
    /// or additional sizes). Returns the primary <see cref="IFileEntry"/> and a list of variant
    /// entries that were created by the provider.
    /// </summary>
    /// <typeparam name="T">Entity type the file is associated with.</typeparam>
    /// <param name="file">The uploaded form file to store.</param>
    /// <param name="category">Logical category or module name used to organize storage (required).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to a tuple containing the primary entry and created variants.</returns>
    Task<(IFileEntry Primary, List<IFileEntry> Variants)> CreateWithVariantsAsync<T>(IFormFile file, string? category, CancellationToken cancellationToken = default)
    where T : class;
}





/// <summary>
/// DI-friendly registration descriptor that ties a StorageProvider instance to a concrete manager Type.
/// This is useful when wiring providers in Startup/Program.cs so the registry and façade can resolve managers easily.
/// </summary>
public class StorageProviderRegistration
{
    public StorageProvider Provider { get; }
    public Type? ManagerType { get; }

    public StorageProviderRegistration(StorageProvider provider, Type? managerType = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ManagerType = managerType;
    }
}



/// <summary>
/// Registry for provider definitions (register, find, update options). This in-memory registry can be seeded via DI
/// using StorageProviderRegistration instances or at runtime via Register.
/// </summary>
public interface IStorageProviderRegistry
{
    IReadOnlyCollection<StorageProvider> GetAll();
    StorageProvider? GetById(string id);
    StorageProvider? GetById(Guid id);
    StorageProvider? GetDefault();
    void Register(StorageProvider provider);
    bool TryUpdateOptions(string id, object options);
    bool TryGetOptions<TOptions>(string id, out TOptions? options) where TOptions : class;
}



/// <summary>
/// Optional non-generic hook: managers can implement this to receive runtime option updates.
/// </summary>
public interface IConfigurableProvider
{
    void UpdateOptions(object options);
}

/// <summary>
/// Optional strongly-typed hook: implement this if you want typed option updates.
/// </summary>
/// <typeparam name="TOptions"></typeparam>
public interface IConfigurableProvider<TOptions>
{
    void UpdateOptions(TOptions options);
}

/// <summary>
/// Simple in-memory storage provider registry.
/// - Can be seeded via DI by registering one or more StorageProviderRegistration instances (TryAddEnumerable).
/// - Allows runtime Register/Update of providers.
/// - Best-effort notifies manager instances resolved from DI that implement IConfigurableProvider.
/// </summary>
public class StorageProviderRegistry : IStorageProviderRegistry
{
    private readonly ConcurrentDictionary<Guid, StorageProvider> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _sp;

    public StorageProviderRegistry(IServiceProvider serviceProvider, IEnumerable<StorageProviderRegistration>? registrations = null)
    {
        _sp = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        if (registrations != null)
        {
            foreach (var r in registrations)
            {
                if (r?.Provider == null) continue;
                _map[r.Provider.Id] = r.Provider;

                // If registration included a manager type and provider.ManagerType not set, store it for future notifications.
                if (!string.IsNullOrWhiteSpace(r.Provider.ManagerType) == false && r.ManagerType != null)
                    r.Provider.ManagerType = r.ManagerType.AssemblyQualifiedName;
            }
        }
    }

    public IReadOnlyCollection<StorageProvider> GetAll() => _map.Values.ToList().AsReadOnly();

    public StorageProvider? GetById(string id)
    {
        if (!Guid.TryParse(id, out var g)) return null;
        return GetById(g);
    }

    public StorageProvider? GetById(Guid id)
    {
        return _map.TryGetValue(id, out var p) ? p : null;
    }

    public StorageProvider? GetDefault()
    {
        var all = _map.Values;
        var d = all.FirstOrDefault(p => p.IsDefault);
        return d ?? all.FirstOrDefault();
    }

    public void Register(StorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _map[provider.Id] = provider;
    }

    public bool TryUpdateOptions(string id, object options)
    {
        if (!Guid.TryParse(id, out var g)) return false;

        if (!_map.TryGetValue(g, out var provider)) return false;

        provider.Options = options;
        provider.OptionsType = options?.GetType().AssemblyQualifiedName;

        // try to notify manager instance (best-effort)
        if (!string.IsNullOrWhiteSpace(provider.ManagerType))
        {
            try
            {
                var mgrType = Type.GetType(provider.ManagerType);
                if (mgrType != null)
                {
                    var mgr = _sp.GetService(mgrType);
                    if (mgr is IConfigurableProvider cfg)
                    {
                        cfg.UpdateOptions(options);
                    }
                    else
                    {
                        var iface = typeof(IConfigurableProvider<>).MakeGenericType(options.GetType());
                        if (iface.IsInstanceOfType(mgr))
                        {
                            var mi = iface.GetMethod("UpdateOptions");
                            mi?.Invoke(mgr, new[] { options });
                        }
                    }
                }
            }
            catch
            {
                // best-effort notification; swallow exceptions
            }
        }

        return true;
    }

    public bool TryGetOptions<TOptions>(string id, out TOptions? options) where TOptions : class
    {
        options = null;
        if (!Guid.TryParse(id, out var g)) return false;

        if (!_map.TryGetValue(g, out var provider)) return false;

        if (provider.Options is TOptions typed)
        {
            options = typed;
            return true;
        }

        // if Options is null or not typed as requested, fail (no DB/JSON path here)
        return false;
    }
}


/// <summary>
/// Helpers to register providers and the in-memory registry.
/// - Register StorageProviderRegistration(s) so StorageProviderRegistry can be seeded from DI.
/// - Register the registry as a singleton.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add the in-memory StorageProviderRegistry and optionally seed it using StorageProviderRegistration entries provided in DI.
    /// Call AddStorageProvider(...) to add registrations before calling this.
    /// </summary>
    public static IServiceCollection AddStorageProviderRegistry(this IServiceCollection services)
    {
        services.TryAddSingleton<IStorageProviderRegistry, StorageProviderRegistry>(sp =>
        {
            // resolve any registrations that were added
            var regs = sp.GetService<IEnumerable<StorageProviderRegistration>>();
            return new StorageProviderRegistry(sp, regs);
        });

        return services;
    }

    /// <summary>
    /// Register a provider + optional manager type into the DI collection so the registry will be seeded on startup.
    /// Also registers the managerType in DI (transient) if provided.
    /// </summary>
    public static IServiceCollection AddStorageProvider(this IServiceCollection services, StorageProvider provider, Type? managerType = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // register the provider registration for the registry to pick up
        services.TryAddEnumerable(ServiceDescriptor.Singleton(new StorageProviderRegistration(provider, managerType)));

        if (managerType != null)
        {
            // register managerType as transient by default (consumer can override with explicit registration)
            services.TryAddTransient(managerType);
        }

        return services;
    }
}


