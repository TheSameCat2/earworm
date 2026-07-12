using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Earworm.Infrastructure;

/// <summary>
/// Thrown when code attempts to resolve per-guild state after that guild has
/// been suspended. Callers must explicitly unblock a guild when it is
/// re-admitted.
/// </summary>
public sealed class GuildAccessBlockedException : InvalidOperationException
{
    public GuildAccessBlockedException(string guildId)
        : base($"Guild '{guildId}' is suspended; per-guild services cannot be created.")
    {
        GuildId = guildId;
    }

    public string GuildId { get; }
}

/// <summary>
/// Lazily creates and caches one <typeparamref name="T"/> instance per guild.
/// Construction, initialization, blocking, and eviction are serialized so a
/// suspension cannot race a factory and leave an orphaned subscribed engine.
/// </summary>
public sealed class PerGuildRegistry<T> : IDisposable where T : class
{
    private readonly Func<string, T> _factory;
    private readonly Dictionary<string, T> _instances = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedGuilds = new(StringComparer.Ordinal);
    private readonly List<Action<T>> _initializers = new();
    private readonly List<T> _created = new();
    private readonly object _lock = new();
    private bool _disposed;

    public PerGuildRegistry(Func<string, T> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Returns the guild's instance, constructing and initializing it exactly
    /// once. Numeric aliases are canonicalized before lookup.
    /// </summary>
    public T GetOrCreate(string guildId)
    {
        var key = NormalizeKey(guildId);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfBlocked(key);
            if (_instances.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var instance = _factory(key);
            _instances.Add(key, instance);
            _created.Add(instance);

            try
            {
                foreach (var initializer in _initializers)
                {
                    initializer(instance);
                }
            }
            catch
            {
                RemoveAndDispose(key, instance);
                throw;
            }

            // Initializers are allowed to perform registry lifecycle work. If
            // one blocked or evicted this guild re-entrantly, never hand the
            // caller an already-retired instance.
            if (_blockedGuilds.Contains(key))
            {
                RemoveAndDispose(key, instance);
                throw new GuildAccessBlockedException(key);
            }

            if (!_instances.TryGetValue(key, out var published) || !ReferenceEquals(published, instance))
            {
                RemoveAndDispose(key, instance);
                throw new InvalidOperationException($"Guild '{key}' was evicted while its services were being initialized.");
            }

            return instance;
        }
    }

    /// <summary>
    /// Returns the guild's instance only if it has already been constructed,
    /// without triggering construction. This remains available while blocked
    /// so lifecycle code can stop the existing engine before evicting it.
    /// </summary>
    public bool TryGet(string guildId, out T instance)
    {
        var key = NormalizeKey(guildId);
        lock (_lock)
        {
            if (_disposed)
            {
                instance = null!;
                return false;
            }

            if (_instances.TryGetValue(key, out var existing))
            {
                instance = existing;
                return true;
            }

            instance = null!;
            return false;
        }
    }

    public IReadOnlyList<T> CreatedInstances()
    {
        lock (_lock)
        {
            if (_disposed) return Array.Empty<T>();
            return _created.ToArray();
        }
    }

    /// <summary>
    /// Registers a callback run exactly once against every existing instance
    /// and once for each future instance.
    /// </summary>
    public void AddInitializer(Action<T> initializer)
    {
        if (initializer is null) throw new ArgumentNullException(nameof(initializer));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _initializers.Add(initializer);
            foreach (var instance in _created.ToArray())
            {
                initializer(instance);
            }
        }
    }

    /// <summary>
    /// Prevents future <see cref="GetOrCreate"/> calls for a guild. Existing
    /// instances remain reachable through <see cref="TryGet"/> until lifecycle
    /// teardown calls <see cref="Evict"/>.
    /// </summary>
    public bool Block(string guildId)
    {
        var key = NormalizeKey(guildId);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _blockedGuilds.Add(key);
        }
    }

    /// <summary>Allows a deliberately re-admitted guild to create services again.</summary>
    public bool Unblock(string guildId)
    {
        var key = NormalizeKey(guildId);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _blockedGuilds.Remove(key);
        }
    }

    public bool IsBlocked(string guildId)
    {
        var key = NormalizeKey(guildId);
        lock (_lock)
        {
            if (_disposed) return true;
            return _blockedGuilds.Contains(key);
        }
    }

    /// <summary>
    /// Removes and disposes a guild's constructed instance. Eviction does not
    /// alter its blocked state: suspension uses Block + Evict, while ordinary
    /// cache replacement may evict without preventing a later recreation.
    /// </summary>
    public bool Evict(string guildId)
    {
        var key = NormalizeKey(guildId);
        lock (_lock)
        {
            if (_disposed) return false;
            if (!_instances.Remove(key, out var instance))
            {
                return false;
            }

            _created.Remove(instance);
            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }

            return true;
        }
    }

    /// <summary>
    /// Retires and disposes every constructed per-guild service. The DI
    /// container owns each registry rather than the values created by its
    /// factory, so without this hook PlayerEngine event subscriptions and
    /// background work would otherwise survive until process termination.
    /// </summary>
    public void Dispose()
    {
        T[] instances;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            instances = new HashSet<T>(_created, ReferenceEqualityComparer.Instance).ToArray();
            _instances.Clear();
            _created.Clear();
            _initializers.Clear();
            _blockedGuilds.Clear();
        }

        foreach (var instance in instances)
        {
            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private void ThrowIfBlocked(string key)
    {
        if (_blockedGuilds.Contains(key))
        {
            throw new GuildAccessBlockedException(key);
        }
    }

    private void RemoveAndDispose(string key, T instance)
    {
        var owned = false;
        if (_instances.TryGetValue(key, out var current) && ReferenceEquals(current, instance))
        {
            _instances.Remove(key);
            owned = true;
        }

        owned |= _created.Remove(instance);
        if (owned && instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static string NormalizeKey(string guildId)
    {
        if (guildId is null) throw new ArgumentNullException(nameof(guildId));

        return ulong.TryParse(guildId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : guildId;
    }
}
