using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Earworm.Infrastructure;

/// <summary>
/// Lazily creates and caches one <typeparamref name="T"/> instance per guild.
/// This is the single abstraction the multi-tenant model leans on: every
/// stateful per-guild engine (PlayerEngine, QueueManager, DJEngine, ...) is
/// resolved through a registry keyed by Discord guild id.
///
/// Two correctness properties matter here:
///   1. <b>Exactly-once construction.</b> The value is wrapped in
///      <see cref="Lazy{T}"/> with <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>
///      so a concurrent <see cref="GetOrCreate"/> race can't construct two
///      instances and discard one. That would be catastrophic for engines that
///      subscribe to Lavalink events in their constructor - the discarded
///      instance's subscription would leak and double-handle events.
///   2. <b>Initializer exactly-once.</b> Global singletons (VoiceManager,
///      TrackFailureHandler, NowPlayingPoster) need to attach to every per-guild
///      engine's events, including engines created before AND after they
///      register. <see cref="AddInitializer"/> runs the callback against all
///      existing instances and every future one, exactly once each, by holding
///      a single lock across both the create path and the register path.
/// </summary>
public sealed class PerGuildRegistry<T> where T : class
{
    private readonly Func<string, T> _factory;
    private readonly ConcurrentDictionary<string, Lazy<T>> _instances = new();
    private readonly List<Action<T>> _initializers = new();
    private readonly List<T> _created = new();
    private readonly object _initLock = new();
    private int _createReentrantGuard;

    public PerGuildRegistry(Func<string, T> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Returns the guild's instance, constructing it on first access.
    /// </summary>
    public T GetOrCreate(string guildId)
    {
        var lazy = _instances.GetOrAdd(
            guildId,
            gid => new Lazy<T>(() => Create(gid), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    /// <summary>
    /// Returns the guild's instance only if it has already been constructed,
    /// without triggering construction.
    /// </summary>
    public bool TryGet(string guildId, out T instance)
    {
        if (_instances.TryGetValue(guildId, out var lazy) && lazy.IsValueCreated)
        {
            instance = lazy.Value;
            return true;
        }
        instance = null!;
        return false;
    }

    /// <summary>
    /// Snapshot of all instances constructed so far.
    /// </summary>
    public IReadOnlyList<T> CreatedInstances()
    {
        lock (_initLock)
        {
            return _created.ToArray();
        }
    }

    /// <summary>
    /// Registers a callback run exactly once against every instance - those
    /// already created and all future ones. The factory itself runs outside the
    /// lock; only the cheap bookkeeping and initializer invocation are guarded,
    /// which is safe because initializers only do non-reentrant work like
    /// <c>engine.TrackStarted += handler</c>.
    /// </summary>
    public void AddInitializer(Action<T> initializer)
    {
        if (initializer is null) throw new ArgumentNullException(nameof(initializer));
        lock (_initLock)
        {
            _initializers.Add(initializer);
            foreach (var inst in _created)
            {
                initializer(inst);
            }
        }
    }

    /// <summary>
    /// Removes a guild's instance and disposes it if it is <see cref="IDisposable"/>. 
    /// Called when a tenant is removed so per-guild engines — and their
    /// subscriptions to shared singletons like the audio service — don't linger
    /// for the process lifetime. Returns true if a constructed instance was
    /// dropped (and disposed).
    /// </summary>
    /// <remarks>
    /// <para>SAFETY: Initializers registered via <see cref="AddInitializer"/> must
    /// NOT call Evict on the same guild being constructed — doing so would cause
    /// infinite recursion (lazy.Value → Create → Evict → lazy.Value → …).
    /// Evict on a different guild is safe because it blocks on <c>_initLock</c>
    /// until the outer Create finishes its initializer loop.</para>
    /// <para>If called re-entrantly from an initializer (detected via
    /// <c>_createReentrantGuard</c>), the instance is removed from
    /// <c>_instances</c> but NOT resolved from the Lazy (to avoid recursion)
    /// and NOT disposed here. The outer Create owns disposal and cleanup.
    /// The re-created instance from a later GetOrCreate will function correctly.</para>
    /// </remarks>
    public bool Evict(string guildId)
    {
        if (!_instances.TryRemove(guildId, out var lazy)) return false;

        bool isReentrant;
        lock (_initLock)
        {
            isReentrant = _createReentrantGuard > 0;
        }

        if (isReentrant)
        {
            // Don't touch the Lazy — resolving it would re-enter Create and
            // cause infinite recursion on this thread. The instance has already
            // been or is about to be added to _created by the outer Create.
            // It was removed from _instances above, so GetOrCreate will make
            // a fresh instance next time.
            return true;
        }

        // Force resolution rather than checking IsValueCreated. If another thread
        // is mid-construction inside GetOrCreate, IsValueCreated is still false but
        // the factory is about to publish the instance into _created — skipping it
        // here would orphan that instance (left in _created, subscribed to the
        // shared audio service, never disposed). Lazy is ExecutionAndPublication,
        // so .Value blocks until the in-flight construction completes and returns
        // the one instance every caller sees.
        var instance = lazy.Value;
        lock (_initLock)
        {
            _created.Remove(instance);

            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        return true;
    }

    private T Create(string guildId)
    {
        var instance = _factory(guildId);
        lock (_initLock)
        {
            // Re-entrancy guard: if an initializer calls Evict() on another
            // guild, that eviction will try to acquire _initLock. Because
            // Monitor allows re-entrancy on the same thread, the eviction
            // succeeds — but it must NOT call initializers on the instance
            // being created here (that would cause infinite recursion or
            // a double-subscription). The guard is checked in Evict().
            _createReentrantGuard++;
            try
            {
                _created.Add(instance);
                foreach (var init in _initializers)
                {
                    init(instance);
                }
            }
            finally
            {
                _createReentrantGuard--;
            }
        }
        return instance;
    }
}
