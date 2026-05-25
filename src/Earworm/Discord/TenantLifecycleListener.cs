using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using Earworm.Config;
using Earworm.Discord.Commands;
using Earworm.Domain.Tenants;

namespace Earworm.Discord;

/// <summary>
/// Manages per-guild slash command registration in response to tenant lifecycle
/// events (startup, add-server, remove-server) and Discord gateway guild events.
/// Must be eagerly resolved from DI before the gateway connects so ctor-time
/// event subscriptions are wired before GuildAvailable/GuildCreated can fire.
/// </summary>
public sealed class TenantLifecycleListener : IDisposable
{
    private readonly DiscordClient _discordClient;
    private readonly ITenantService _tenantService;
    private readonly EarwormConfig _config;
    private readonly ILogger<TenantLifecycleListener> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public TenantLifecycleListener(
        DiscordClient discordClient,
        ITenantService tenantService,
        EarwormConfig config,
        ILogger<TenantLifecycleListener> logger)
    {
        _discordClient = discordClient;
        _tenantService = tenantService;
        _config = config;
        _logger = logger;

        _discordClient.GuildCreated += OnGuildCreatedAsync;
        _discordClient.GuildDeleted += OnGuildDeletedAsync;
        _discordClient.GuildAvailable += OnGuildAvailableAsync;
    }

    /// <summary>
    /// Called at startup (before gateway.StartAsync) to register slash commands for
    /// all active tenants. AdminCommands is pinned to the control guild only.
    /// DSharpPlus pushes all pending registrations automatically when Ready fires.
    /// </summary>
    public async Task RegisterStartupCommandsAsync()
    {
        var slash = _discordClient.GetExtension<SlashCommandsExtension>();
        var tenants = await _tenantService.GetAllTenantsAsync();
        var active = tenants.Where(t => t.Status == "active").ToList();

        foreach (var tenant in active)
        {
            if (!ulong.TryParse(tenant.GuildId, out var guildId)) continue;
            RegisterPerGuildCommands(slash, guildId);
        }

        if (ulong.TryParse(_config.Discord.GuildId, out var controlGuildId))
            slash.RegisterCommands<AdminCommands>(controlGuildId);

        _logger.LogInformation(
            "Queued startup slash command registration for {Count} active tenant(s).",
            active.Count);
    }

    /// <summary>
    /// Called by AdminCommands after a tenant is added. Registers commands for the
    /// new guild and pushes the update to Discord via RefreshCommands.
    /// </summary>
    public async Task OnTenantAddedAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id))
        {
            _logger.LogWarning("OnTenantAdded: skipping invalid guild ID '{GuildId}'.", guildId);
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            var slash = _discordClient.GetExtension<SlashCommandsExtension>();
            RegisterPerGuildCommands(slash, id);
            await slash.RefreshCommands();
            _logger.LogInformation("Slash commands registered and pushed for new tenant {GuildId}.", guildId);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Called by AdminCommands after a tenant is suspended. Command visibility on
    /// Discord is deferred to PR-3's WhitelistedGuild guard; only logs here.
    /// </summary>
    public Task OnTenantRemovedAsync(string guildId)
    {
        _logger.LogInformation(
            "Tenant {GuildId} suspended. Commands remain registered on Discord; WhitelistedGuild guard will block access after PR-3.",
            guildId);
        return Task.CompletedTask;
    }

    private Task OnGuildCreatedAsync(DiscordClient sender, GuildCreateEventArgs e)
    {
        _logger.LogInformation("Bot added to guild {GuildId} ({GuildName}).", e.Guild.Id, e.Guild.Name);
        return Task.CompletedTask;
    }

    private Task OnGuildDeletedAsync(DiscordClient sender, GuildDeleteEventArgs e)
    {
        _logger.LogInformation("Bot removed from guild {GuildId}.", e.Guild.Id);
        return Task.CompletedTask;
    }

    private Task OnGuildAvailableAsync(DiscordClient sender, GuildCreateEventArgs e)
    {
        _logger.LogDebug("Guild {GuildId} ({GuildName}) became available.", e.Guild.Id, e.Guild.Name);
        return Task.CompletedTask;
    }

    private static void RegisterPerGuildCommands(SlashCommandsExtension slash, ulong guildId)
    {
        slash.RegisterCommands<PlaybackCommands>(guildId);
        slash.RegisterCommands<QueueCommands>(guildId);
        slash.RegisterCommands<InfoCommands>(guildId);
        slash.RegisterCommands<DJCommands>(guildId);
        slash.RegisterCommands<ConfigCommands>(guildId);
    }

    public void Dispose()
    {
        _discordClient.GuildCreated -= OnGuildCreatedAsync;
        _discordClient.GuildDeleted -= OnGuildDeletedAsync;
        _discordClient.GuildAvailable -= OnGuildAvailableAsync;
        _refreshLock.Dispose();
    }
}
