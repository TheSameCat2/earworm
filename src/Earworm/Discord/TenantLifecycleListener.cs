using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using Earworm.Discord.Commands;
using Earworm.Domain.DJ;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Domain.Tenants;
using Earworm.Infrastructure;
using Lavalink4NET;
using Lavalink4NET.Players;
using Microsoft.Extensions.Logging;

namespace Earworm.Discord;

/// <summary>
/// Owns admission-time command registration and suspension-time teardown for
/// every tenant guild.
/// </summary>
public sealed class TenantLifecycleListener
{
    private readonly ITenantService _tenants;
    private readonly DiscordClient _discordClient;
    private readonly IAudioService _audioService;
    private readonly PerGuildRegistry<PlayerEngine> _players;
    private readonly PerGuildRegistry<QueueManager> _queues;
    private readonly PerGuildRegistry<DJEngine> _djEngines;
    private readonly PerGuildRegistry<AudioTransitionController> _transitions;
    private readonly VoiceManager _voiceManager;
    private readonly ILogger<TenantLifecycleListener> _logger;
    private readonly ShutdownLifetime _shutdown;

    // DSharpPlus 4.x has no local unregister operation. Definitions loaded into
    // the extension must remain distinct from tenants that are currently active.
    // A re-admit refreshes the retained definition instead of registering it a
    // second time.
    private readonly HashSet<ulong> _moduleGuilds = new();
    private readonly HashSet<ulong> _activeGuilds = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private SlashCommandsExtension? _slash;
    private bool _globalAdminRegistered;
    private CancellationTokenSource? _commandCleanupCts;

    public TenantLifecycleListener(
        ITenantService tenants,
        DiscordClient discordClient,
        IAudioService audioService,
        PerGuildRegistry<PlayerEngine> players,
        PerGuildRegistry<QueueManager> queues,
        PerGuildRegistry<DJEngine> djEngines,
        PerGuildRegistry<AudioTransitionController> transitions,
        VoiceManager voiceManager,
        ShutdownLifetime shutdown,
        ILogger<TenantLifecycleListener> logger)
    {
        _tenants = tenants;
        _discordClient = discordClient;
        _audioService = audioService;
        _players = players;
        _queues = queues;
        _djEngines = djEngines;
        _transitions = transitions;
        _voiceManager = voiceManager;
        _shutdown = shutdown;
        _logger = logger;
        _discordClient.Ready += OnDiscordReadyAsync;
    }

    /// <summary>Binds the slash-command extension created by the composition root.</summary>
    public void Attach(SlashCommandsExtension slash)
    {
        _slash = slash ?? throw new ArgumentNullException(nameof(slash));
    }

    /// <summary>
    /// Registers the owner-only admin group globally only as a recovery path
    /// when the database starts with no active tenant (for example after manual
    /// repair). Normal operation keeps it guild-scoped and refuses to suspend
    /// the final active tenant.
    /// </summary>
    private void RegisterGlobalAdminCommands()
    {
        var slash = _slash ?? throw new InvalidOperationException("Attach() must be called before registering commands.");
        lock (_lock)
        {
            if (_globalAdminRegistered) return;
            slash.RegisterCommands<AdminCommands>();
            _globalAdminRegistered = true;
        }
    }

    /// <summary>
    /// Normalizes legacy tenant aliases, blocks suspended guild registries, and
    /// registers command modules for every active tenant. Call before connecting
    /// the gateway so DSharpPlus publishes the complete active set on Ready.
    /// </summary>
    public async Task RegisterStartupCommandsAsync()
    {
        var slash = _slash ?? throw new InvalidOperationException("Attach() must be called before registering commands.");

        var normalizedCount = await _tenants.NormalizeLegacyGuildIdsAsync();
        if (normalizedCount > 0)
        {
            _logger.LogWarning("Canonicalized {Count} legacy tenant guild ID alias(es).", normalizedCount);
        }

        var tenants = await _tenants.GetAllTenantsAsync();
        if (!tenants.Any(tenant => string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase)))
        {
            // Recovery-only global registration. Normal deployments keep every
            // module on one guild-scoped target, avoiding DSharpPlus 4.x's
            // duplicate global+guild registration race. The final active tenant
            // cannot be suspended through the command path below.
            RegisterGlobalAdminCommands();
        }

        var count = 0;
        foreach (var tenant in tenants)
        {
            var canonicalGuildId = DiscordGuildId.Normalize(tenant.GuildId, nameof(tenant.GuildId));
            var numericGuildId = ulong.Parse(canonicalGuildId);
            if (string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                _voiceManager.AllowGuildTimers(numericGuildId);
                UnblockRegistries(canonicalGuildId);
                lock (_lock) _activeGuilds.Add(numericGuildId);
                RegisterModulesForGuild(slash, numericGuildId);
                count++;
            }
            else
            {
                _voiceManager.BlockGuildTimers(numericGuildId);
                BlockRegistries(canonicalGuildId);
                lock (_lock) _activeGuilds.Remove(numericGuildId);
            }
        }

        _logger.LogInformation("Registered slash commands for {Count} active tenant(s).", count);
    }

    /// <summary>
    /// Admits a tenant, unblocks its registries, and publishes its guild command
    /// set. Re-admission preserves all state retained during suspension.
    /// </summary>
    public async Task AdmitGuildAsync(string guildId, string? ownerUserId)
    {
        var canonicalGuildId = DiscordGuildId.Normalize(guildId, nameof(guildId));
        await _lifecycleGate.WaitAsync();
        try
        {
            await _tenants.AddTenantAsync(canonicalGuildId, ownerUserId);
            await RegisterForGuildCoreAsync(canonicalGuildId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Registers commands for a tenant admitted while the process is running.
    /// A failed live refresh is non-fatal; startup registration retries it.
    /// </summary>
    public async Task RegisterForGuildAsync(string guildId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await RegisterForGuildCoreAsync(guildId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RegisterForGuildCoreAsync(string guildId)
    {
        var canonicalGuildId = DiscordGuildId.Normalize(guildId, nameof(guildId));
        var numericGuildId = ulong.Parse(canonicalGuildId);

        _voiceManager.AllowGuildTimers(numericGuildId);
        UnblockRegistries(canonicalGuildId);
        lock (_lock) _activeGuilds.Add(numericGuildId);

        var slash = _slash;
        if (slash is null)
        {
            _logger.LogWarning(
                "Tenant {GuildId} added before slash extension was attached; commands register on next restart.",
                canonicalGuildId);
            return;
        }

        RegisterModulesForGuild(slash, numericGuildId);
        try
        {
            // RefreshCommands republishes every definition retained by the
            // extension, including definitions for a guild suspended earlier in
            // this process. Re-clear inactive guilds immediately afterward.
            await slash.RefreshCommands();
            await ClearSuspendedGuildCommandsCoreAsync();
            ScheduleSuspendedCommandCleanup();
            _logger.LogInformation(
                "Registered and refreshed slash commands for newly admitted guild {GuildId}.",
                numericGuildId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Live slash-command refresh failed for guild {GuildId}; will register on next restart.",
                numericGuildId);
        }
    }

    /// <summary>
    /// Blocks fresh service resolution before changing persistence, then tears
    /// down the tenant. A failed database update restores access for a tenant
    /// that was active when the operation began.
    /// </summary>
    public async Task SuspendGuildAsync(string guildId)
    {
        var canonicalGuildId = DiscordGuildId.Normalize(guildId, nameof(guildId));
        await _lifecycleGate.WaitAsync();
        try
        {
            var wasActive = await _tenants.IsAdmittedAsync(canonicalGuildId);
            if (!wasActive)
            {
                throw new InvalidOperationException($"Guild '{canonicalGuildId}' is not an active tenant.");
            }

            var tenants = await _tenants.GetAllTenantsAsync();
            var activeCount = tenants.Count(tenant =>
                string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase));
            if (activeCount <= 1)
            {
                throw new InvalidOperationException(
                    "The final active tenant cannot be suspended. Admit another server first.");
            }

            BlockRegistries(canonicalGuildId);

            try
            {
                await _tenants.RemoveTenantAsync(canonicalGuildId);
            }
            catch
            {
                if (wasActive) UnblockRegistries(canonicalGuildId);
                throw;
            }

            await DeregisterGuildCoreAsync(canonicalGuildId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Stops playback, disconnects voice, evicts per-guild engines, and removes
    /// the guild's remote command set. This method assumes persistence has
    /// already been suspended; <see cref="SuspendGuildAsync"/> orchestrates the
    /// complete admin operation.
    /// </summary>
    public async Task DeregisterGuildAsync(string guildId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await DeregisterGuildCoreAsync(guildId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task DeregisterGuildCoreAsync(string guildId)
    {
        var canonicalGuildId = DiscordGuildId.Normalize(guildId, nameof(guildId));
        var numericGuildId = ulong.Parse(canonicalGuildId);

        // Block first so work arriving while network teardown awaits cannot
        // recreate an engine that is about to be evicted.
        BlockRegistries(canonicalGuildId);
        lock (_lock) _activeGuilds.Remove(numericGuildId);

        // An expiry callback that already won cancellation may still be inside
        // LeaveChannelAsync. Drain it before retiring engines, and keep timer
        // creation blocked until a serialized re-admission explicitly allows it.
        await _voiceManager.CancelGuildTimersAndDrainAsync(numericGuildId);

        if (_players.TryGet(canonicalGuildId, out var engine))
        {
            try
            {
                await engine.RetireAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping player for removed guild {GuildId}.", numericGuildId);
            }
        }

        if (_queues.TryGet(canonicalGuildId, out var queue))
        {
            try
            {
                await queue.RetireAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error draining queue work for removed guild {GuildId}.", numericGuildId);
            }
        }

        try
        {
            var lavalinkPlayer = await _audioService.Players.GetPlayerAsync<LavalinkPlayer>(numericGuildId);
            if (lavalinkPlayer is not null)
            {
                await lavalinkPlayer.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting voice for removed guild {GuildId}.", numericGuildId);
        }

        _players.Evict(canonicalGuildId);
        _queues.Evict(canonicalGuildId);
        _djEngines.Evict(canonicalGuildId);
        _transitions.Evict(canonicalGuildId);

        await ClearGuildCommandsBestEffortAsync(numericGuildId);
        // A transient Discord REST failure (or a detached DSharpPlus publish
        // already in flight) can leave stale definitions visible after the
        // first clear. Authorization is already blocked; retry cleanup for UX.
        ScheduleSuspendedCommandCleanup();
        _logger.LogInformation("Tore down per-guild engines for removed guild {GuildId}.", numericGuildId);
    }

    /// <summary>
    /// Clears command sets for suspended tenants. The composition root should
    /// call this once after the gateway is connected to remove commands left by
    /// a suspension performed by an older release or during an outage.
    /// </summary>
    public async Task ClearSuspendedGuildCommandsAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await ClearSuspendedGuildCommandsCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ClearSuspendedGuildCommandsCoreAsync()
    {
        var tenants = await _tenants.GetAllTenantsAsync();
        var inactiveGuildIds = tenants
            .Where(tenant => !string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase))
            .Select(tenant => DiscordGuildId.Normalize(tenant.GuildId, nameof(tenant.GuildId)))
            .Select(ulong.Parse)
            .ToArray();

        foreach (var guildId in inactiveGuildIds)
        {
            await ClearGuildCommandsBestEffortAsync(guildId);
        }
    }

    private Task OnDiscordReadyAsync(DiscordClient sender, ReadyEventArgs e)
    {
        // DSharpPlus republishes registered modules from detached tasks after
        // Ready. Definitions retained for a suspended guild can therefore land
        // after an immediate clear; repeat cleanup after the publish wave.
        ScheduleSuspendedCommandCleanup();
        return Task.CompletedTask;
    }

    private void ScheduleSuspendedCommandCleanup()
    {
        CancellationTokenSource cleanupCts;
        lock (_lock)
        {
            _commandCleanupCts?.Cancel();
            cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _commandCleanupCts = cleanupCts;
        }

        var cancellationToken = cleanupCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // RefreshCommands/Ready return before their per-guild REST jobs
                // finish. Coalesce bursts into one retry loop, with later passes
                // covering ordinary latency and rate-limit delay. Authorization
                // checks remain the fail-closed backstop between passes.
                foreach (var delay in new[]
                {
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(45),
                    TimeSpan.FromMinutes(3),
                    TimeSpan.FromMinutes(10)
                })
                {
                    await Task.Delay(delay, cancellationToken);
                    await ClearSuspendedGuildCommandsAsync();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal process shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deferred suspended-guild command cleanup failed; the next Ready event will retry.");
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_commandCleanupCts, cleanupCts))
                    {
                        _commandCleanupCts = null;
                    }
                }
                cleanupCts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void RegisterModulesForGuild(SlashCommandsExtension slash, ulong guildId)
    {
        lock (_lock)
        {
            if (_moduleGuilds.Contains(guildId)) return;

            slash.RegisterCommands<PlaybackCommands>(guildId);
            slash.RegisterCommands<QueueCommands>(guildId);
            slash.RegisterCommands<InfoCommands>(guildId);
            slash.RegisterCommands<DJCommands>(guildId);
            slash.RegisterCommands<ConfigCommands>(guildId);
            if (!_globalAdminRegistered)
            {
                slash.RegisterCommands<AdminCommands>(guildId);
            }
            _moduleGuilds.Add(guildId);
        }
    }

    private void BlockRegistries(string guildId)
    {
        _players.Block(guildId);
        _queues.Block(guildId);
        _djEngines.Block(guildId);
        _transitions.Block(guildId);
    }

    private void UnblockRegistries(string guildId)
    {
        _players.Unblock(guildId);
        _queues.Unblock(guildId);
        _djEngines.Unblock(guildId);
        _transitions.Unblock(guildId);
    }

    private async Task ClearGuildCommandsBestEffortAsync(ulong guildId)
    {
        try
        {
            // An empty bulk overwrite is Discord's supported operation for
            // deleting every guild-scoped command owned by this application.
            await _discordClient.BulkOverwriteGuildApplicationCommandsAsync(
                guildId,
                Array.Empty<DiscordApplicationCommand>());
            _logger.LogInformation("Cleared remote slash commands for suspended guild {GuildId}.", guildId);
        }
        catch (Exception ex)
        {
            // Authorization checks still reject interactions for a suspended
            // tenant. Keep teardown successful and retry cleanup later.
            _logger.LogWarning(ex, "Could not clear remote slash commands for suspended guild {GuildId}.", guildId);
        }
    }
}
