using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Earworm.Infrastructure;

namespace Earworm.Tests.Infrastructure;

public sealed class PerGuildRegistryTests
{
    /// <summary>Minimal IDisposable payload that records its own lifecycle.</summary>
    private sealed class Tracker : IDisposable
    {
        public string GuildId { get; }
        public bool Disposed { get; private set; }
        public Tracker(string guildId) => GuildId = guildId;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void GetOrCreate_ReturnsSameInstancePerGuild_AndDistinctAcrossGuilds()
    {
        var reg = new PerGuildRegistry<Tracker>(gid => new Tracker(gid));

        var a1 = reg.GetOrCreate("a");
        var a2 = reg.GetOrCreate("a");
        var b = reg.GetOrCreate("b");

        a1.Should().BeSameAs(a2, "a guild's instance is cached");
        b.Should().NotBeSameAs(a1);
        b.GuildId.Should().Be("b", "the factory receives the guild id");
    }

    [Fact]
    public void Evict_DisposesInstance_AndRemovesItFromTheRegistry()
    {
        var reg = new PerGuildRegistry<Tracker>(gid => new Tracker(gid));
        var inst = reg.GetOrCreate("g");
        reg.CreatedInstances().Should().Contain(inst);

        var evicted = reg.Evict("g");

        evicted.Should().BeTrue();
        inst.Disposed.Should().BeTrue("Evict must dispose IDisposable instances so leaked subscriptions are released");
        reg.TryGet("g", out _).Should().BeFalse("the instance is gone from the registry");
        reg.CreatedInstances().Should().NotContain(inst);
    }

    [Fact]
    public void Evict_UnknownGuild_ReturnsFalse_AndIsANoOp()
    {
        var reg = new PerGuildRegistry<Tracker>(gid => new Tracker(gid));

        reg.Evict("never-created").Should().BeFalse();
    }

    [Fact]
    public void GetOrCreate_AfterEvict_BuildsAFreshInstance()
    {
        var reg = new PerGuildRegistry<Tracker>(gid => new Tracker(gid));
        var first = reg.GetOrCreate("g");

        reg.Evict("g");
        var second = reg.GetOrCreate("g");

        second.Should().NotBeSameAs(first, "eviction clears the cache so a re-admit gets a clean instance");
        first.Disposed.Should().BeTrue();
        second.Disposed.Should().BeFalse();
    }

    [Fact]
    public void AddInitializer_RunsExactlyOncePerInstance_ForExistingAndFutureGuilds()
    {
        var reg = new PerGuildRegistry<Tracker>(gid => new Tracker(gid));
        var initialized = new List<string>();

        var existing = reg.GetOrCreate("a");          // created BEFORE the initializer
        reg.AddInitializer(t => initialized.Add(t.GuildId));
        initialized.Should().ContainSingle().Which.Should().Be("a",
            "the initializer runs against instances already created");

        _ = reg.GetOrCreate("b");                      // created AFTER the initializer
        initialized.Should().Equal(new[] { "a", "b" },
            "the initializer runs against future instances too, exactly once each");
    }

    [Fact]
    public void Evict_ThenRecreate_ReRunsInitializersOnTheNewInstance()
    {
        // Mirrors VoiceManager/NowPlayingPoster re-attaching to a re-admitted
        // guild's freshly constructed engine after a remove-server eviction.
        var reg = new PerGuildRegistry<Tracker>(gid => new Tracker(gid));
        int initRuns = 0;
        reg.AddInitializer(_ => initRuns++);

        var first = reg.GetOrCreate("g");             // init runs once
        reg.Evict("g");
        var second = reg.GetOrCreate("g");            // init runs again on the new instance

        initRuns.Should().Be(2);
        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public async Task Evict_DuringConcurrentConstruction_DisposesInstance_AndDoesNotOrphanIt()
    {
        // Regression: Evict must resolve an in-flight Lazy rather than skip it on
        // IsValueCreated == false. Otherwise an instance constructed concurrently
        // lands in _created but is gone from _instances — orphaned, subscribed to
        // shared singletons, and never disposed.
        var factoryRunning = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        Tracker? built = null;

        var reg = new PerGuildRegistry<Tracker>(gid =>
        {
            factoryRunning.Set();   // signal: construction is in progress
            release.Wait();         // block until the test lets it finish
            built = new Tracker(gid);
            return built;
        });

        // Thread A: construct "g" — blocks inside the factory.
        var createTask = Task.Run(() => reg.GetOrCreate("g"));
        factoryRunning.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the factory should be running");

        // Thread B: evict "g" while construction is in flight.
        var evictTask = Task.Run(() => reg.Evict("g"));

        // Let construction complete; both threads converge on the one instance.
        release.Set();
        var created = await createTask;
        var evicted = await evictTask;

        evicted.Should().BeTrue("Evict resolves the in-flight Lazy instead of skipping it");
        created.Should().BeSameAs(built);
        built!.Disposed.Should().BeTrue("the constructed instance must be disposed, not orphaned");
        reg.CreatedInstances().Should().NotContain(built, "it must not linger in _created");
        reg.TryGet("g", out _).Should().BeFalse();
    }
}
