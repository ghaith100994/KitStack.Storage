using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KitStack.Abstractions.Instances;

/// <summary>Default <see cref="IFileStorageManagerProvider"/>; see the interface for the behaviour.</summary>
internal sealed partial class FileStorageManagerProvider : IFileStorageManagerProvider, IAsyncDisposable, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, StaticStorageInstance> _statics;
    private readonly StorageInstanceResolver _resolver;
    private readonly StorageInstanceCacheOptions _cache;
    private readonly ILogger _logger;
    private readonly IStorageInstanceSource? _source;
    private readonly bool _tracking;
    private readonly bool _hasUnkeyedManager;
    private readonly ConcurrentDictionary<string, InstanceEntry> _entries = new(StorageInstance.Comparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StorageInstance.Comparer);
    private volatile bool _disposed;

    public FileStorageManagerProvider(
        IServiceProvider services,
        IEnumerable<StaticStorageInstance> statics,
        StorageInstanceResolver resolver,
        IOptions<StorageInstanceCacheOptions> cacheOptions,
        ILogger<FileStorageManagerProvider>? logger = null)
    {
        _services = services;
        _resolver = resolver;
        _cache = cacheOptions.Value;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        // The last registration of a name wins (a second unnamed AddXxx() replaces the default, as in 0.2).
        _statics = new Dictionary<string, StaticStorageInstance>(StorageInstance.Comparer);
        foreach (var instance in statics)
            _statics[StorageInstance.Normalize(instance.Name)] = instance;
        _source = services.GetService<IStorageInstanceSource>();
        _tracking = services.GetService<FileEntryTrackingMarker>() is not null;
        _hasUnkeyedManager = services.GetService<IServiceProviderIsService>()?.IsService(typeof(IFileStorageManager)) == true;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            var names = new HashSet<string>(_statics.Keys, StorageInstance.Comparer);
            if (_hasUnkeyedManager)
                names.Add(StorageInstance.Default);
            names.UnionWith(_entries.Values.Select(e => e.Name));
            return names.Order(StorageInstance.Comparer).ToList();
        }
    }

    /// <inheritdoc/>
    public IFileStorageManager Get(string name)
    {
        var key = StorageInstance.Normalize(name);
        if (_entries.TryGetValue(key, out var entry) && IsFresh(entry))
            return entry.Public;
        return GetEntryAsync(key, CancellationToken.None).AsTask().GetAwaiter().GetResult().Public;
    }

    /// <inheritdoc/>
    public async ValueTask<IFileStorageManager> GetAsync(string name, CancellationToken cancellationToken = default)
        => (await GetEntryAsync(name, cancellationToken).ConfigureAwait(false)).Public;

    /// <inheritdoc/>
    public bool TryGet(string name, [NotNullWhen(true)] out IFileStorageManager? manager)
    {
        try
        {
            manager = Get(name);
            return true;
        }
        catch (StorageInstanceNotFoundException)
        {
            manager = null;
            return false;
        }
    }

    /// <inheritdoc/>
    public StorageCapabilities GetCapabilities(string name)
    {
        var key = StorageInstance.Normalize(name);
        if (!_entries.TryGetValue(key, out var entry))
            entry = GetEntryAsync(key, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return entry.Capabilities;
    }

    /// <inheritdoc/>
    public void Invalidate(string name)
    {
        var key = StorageInstance.Normalize(name);
        if (_entries.TryGetValue(key, out var entry) && !entry.IsStatic && _entries.TryRemove(new KeyValuePair<string, InstanceEntry>(key, entry)))
        {
            entry.Handle?.Retire();
            LogInvalidated(_logger, key);
        }
    }

    /// <inheritdoc/>
    public void InvalidateAll()
    {
        foreach (var name in _entries.Keys)
            Invalidate(name);
    }

    /// <summary>The current build of a runtime or named static instance (lease forwarding target).</summary>
    internal async ValueTask<LeasedFileStorageManager> GetHandleAsync(string name, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(name, cancellationToken).ConfigureAwait(false);
        return entry.Handle ?? throw new ObjectDisposedException($"Storage instance '{name}'");
    }

    /// <summary>Definition context for health probes, without building or caching anything new.</summary>
    internal async Task<ResolvedStorageInstance?> ResolveForProbeAsync(string name, CancellationToken cancellationToken)
    {
        var key = StorageInstance.Normalize(name);
        if (_statics.TryGetValue(key, out var instance))
            return _resolver.ResolveStatic(instance);
        if (_entries.TryGetValue(key, out var entry) && entry.Resolved is not null)
            return entry.Resolved;
        if (_source is not null && await _source.FindAsync(key, cancellationToken).ConfigureAwait(false) is { } definition)
            return await _resolver.ResolveAsync(definition, cancellationToken).ConfigureAwait(false);
        return null;
    }

    internal async ValueTask<InstanceEntry> GetEntryAsync(string name, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = StorageInstance.Normalize(name);
        if (_entries.TryGetValue(key, out var entry) && IsFresh(entry))
            return entry;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out entry) && IsFresh(entry))
                return entry;

            if (entry is null && CreateStaticEntry(key) is { } staticEntry)
            {
                _entries[key] = staticEntry;
                return staticEntry;
            }

            if (_source is null)
                throw NotFound(key);

            var definition = await _source.FindAsync(key, cancellationToken).ConfigureAwait(false);
            if (definition is null)
            {
                if (entry is not null)
                    Invalidate(key);
                throw NotFound(key);
            }

            if (!StorageInstance.AreSame(definition.Name, key))
                throw new StorageConfigurationException($"The storage instance source returned '{definition.Name}' when asked for '{key}'.");

            if (entry is not null && string.Equals(entry.Version, definition.Version, StringComparison.Ordinal))
            {
                entry.Touch();
                return entry;
            }

            var built = await BuildDynamicAsync(definition, cancellationToken).ConfigureAwait(false);
            _entries[key] = built;
            if (entry is null)
            {
                LogBuilt(_logger, built.Name, definition.ProviderType, definition.Version);
            }
            else
            {
                entry.Handle?.Retire();
                LogRebuilt(_logger, built.Name, definition.ProviderType, entry.Version, definition.Version);
            }

            return built;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsFresh(InstanceEntry entry)
        => entry.IsStatic || (_cache.RefreshInterval > TimeSpan.Zero && entry.Age < _cache.RefreshInterval);

    private InstanceEntry? CreateStaticEntry(string key)
    {
        if (_statics.TryGetValue(key, out var instance))
        {
            if (instance.IsDefault)
            {
                var resolved = TryResolveStatic(instance);
                return new InstanceEntry(key, isStatic: true, version: null, handle: null, Track(ResolveDefaultManager(key)),
                    resolved is null ? StorageCapabilities.None : SafeCapabilities(resolved), resolved);
            }

            var named = _resolver.ResolveStatic(instance);
            var errors = StorageInstanceResolver.Validate(named);
            if (errors.Count > 0)
                throw new StorageInstanceValidationException(key, errors);
            var handle = CreateHandle(key, named);
            LogBuilt(_logger, key, instance.ProviderType, null);
            return new InstanceEntry(key, isStatic: true, version: null, handle, Track(handle), SafeCapabilities(named), named);
        }

        if (StorageInstance.IsDefault(key) && _hasUnkeyedManager)
            return new InstanceEntry(key, isStatic: true, version: null, handle: null, Track(ResolveDefaultManager(key)), StorageCapabilities.None, null);

        return null;
    }

    private ResolvedStorageInstance? TryResolveStatic(StaticStorageInstance instance)
    {
        try
        {
            return _resolver.ResolveStatic(instance);
        }
        catch (StorageConfigurationException)
        {
            return null;
        }
    }

    private IFileStorageManager ResolveDefaultManager(string key)
        // With tracking on, the provider's own registration was moved to a keyed slot and the unkeyed one is the scoped decorator.
        => _services.GetKeyedService<IFileStorageManager>(KitStackStorageServiceCollectionExtensions.InnerManagerKey)
           ?? _services.GetService<IFileStorageManager>()
           ?? throw NotFound(key);

    private async Task<InstanceEntry> BuildDynamicAsync(StorageInstanceDefinition definition, CancellationToken cancellationToken)
    {
        var key = StorageInstance.Normalize(definition.Name);
        var resolved = await _resolver.ResolveAsync(definition, cancellationToken).ConfigureAwait(false);
        var errors = StorageInstanceResolver.Validate(resolved);
        if (errors.Count > 0)
            throw new StorageInstanceValidationException(key, errors);
        var handle = CreateHandle(key, resolved);
        return new InstanceEntry(key, isStatic: false, definition.Version, handle, Track(handle), SafeCapabilities(resolved), resolved);
    }

    private LeasedFileStorageManager CreateHandle(string key, ResolvedStorageInstance resolved)
    {
        IFileStorageManager manager;
        try
        {
            manager = resolved.Factory.Create(resolved.Context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The inner exception is dropped on purpose: provider exceptions may quote setting values.
            throw new StorageConfigurationException(resolved.Redactor.Redact(
                $"Storage instance '{key}' ({resolved.Factory.ProviderType}) could not be created: {ex.GetType().Name}: {ex.Message}"));
        }

        return new LeasedFileStorageManager(key, manager, ct => GetHandleAsync(key, ct), _logger);
    }

    private static StorageCapabilities SafeCapabilities(ResolvedStorageInstance resolved)
    {
        try
        {
            return resolved.Factory.GetCapabilities(resolved.Context);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OptionsValidationException)
        {
            return resolved.Factory.Capabilities;
        }
    }

    private IFileStorageManager Track(IFileStorageManager manager)
        => _tracking
            ? new FileEntryTrackingStorageManager(manager, _services.GetRequiredService<IFileEntryStore>(), this)
            : manager;

    private static StorageInstanceNotFoundException NotFound(string key)
        => new($"Storage instance '{key}' is not registered and not defined by an IStorageInstanceSource.") { StorageName = key };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var entry in _entries.Values)
            entry.Handle?.Retire();
        _entries.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        var handles = _entries.Values.Select(e => e.Handle).OfType<LeasedFileStorageManager>().ToList();
        Dispose();
        await Task.WhenAll(handles.Select(h => h.Disposal)).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Built storage instance {StorageName} ({ProviderType}, version {Version})")]
    private static partial void LogBuilt(ILogger logger, string storageName, string providerType, string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilt storage instance {StorageName} ({ProviderType}): version {OldVersion} -> {NewVersion}")]
    private static partial void LogRebuilt(ILogger logger, string storageName, string providerType, string? oldVersion, string? newVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invalidated storage instance {StorageName}")]
    private static partial void LogInvalidated(ILogger logger, string storageName);

    internal sealed class InstanceEntry(
        string name, bool isStatic, string? version, LeasedFileStorageManager? handle, IFileStorageManager @public,
        StorageCapabilities capabilities, ResolvedStorageInstance? resolved)
    {
        private long _checkedAt = Environment.TickCount64;

        public string Name { get; } = name;
        public bool IsStatic { get; } = isStatic;
        public string? Version { get; } = version;
        public LeasedFileStorageManager? Handle { get; } = handle;
        public IFileStorageManager Public { get; } = @public;
        public StorageCapabilities Capabilities { get; } = capabilities;
        public ResolvedStorageInstance? Resolved { get; } = resolved;
        public TimeSpan Age => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _checkedAt));
        public void Touch() => Interlocked.Exchange(ref _checkedAt, Environment.TickCount64);
    }
}
