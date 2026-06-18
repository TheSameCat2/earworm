using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DSharpPlus.SlashCommands;
using Earworm.Discord.Commands;
using Earworm.Domain.Tenants;

namespace Earworm.Discord;

/// <summary>
/// Registers slash commands per active tenant guild rather than globally, so
/// changes propagate instantly (Discord applies guild-scoped commands without
/// the ~1h global cache delay).
///
/// At startup <see cref="RegisterStartupCommandsAsync"/> registers every active
/// tenant's commands before the gateway connects (DSharpPlus pushes the
/// registered set on Ready). When a tenant is added at runtime via
/// <c>/admin add-server</c>, <see cref="RegisterForGuildAsync"/> registers and
/// refreshes that one guild on the fly.
/// </summary>
public sealed class TenantLifecycleListener
{
    private readonly ITenantService _tenants;
    private readonly ILogger<TenantLifecycleListener> _logger;
    private readonly HashSet<ulong> _registeredGuilds = new();
    private readonly object _lock = new();
    private SlashCommandsExtension? _slash;

    public TenantLifecycleListener(ITenantService tenants, ILogger<TenantLifecycleListener> logger)
    {
        _tenants = tenants;
        _logger = logger;
    }

    /// <summary>
    /// Binds the DSharpPlus slash-commands extension, which is created in
    /// Program.Main after the DI container is built (it isn't a DI service).
    /// </summary>
    public void Attach(SlashCommandsExtension slash)
    {
        _slash = slash ?? throw new ArgumentNullException(nameof(slash));
    }

    /// <summary>
    /// Registers commands for every active tenant. Call BEFORE connecting the
    /// gateway so DSharpPlus pushes the full set on Ready.
    /// </summary>
    public async Task RegisterStartupCommandsAsync()
    {
        var slash = _slash ?? throw new InvalidOperationException("Attach() must be called before registering commands.");

        var tenants = await _tenants.GetAllTenantsAsync();
        int count = 0;
        foreach (var t in tenants)
        {
            if (!string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ulong.TryParse(t.GuildId, out var gid)) continue;
            RegisterModulesForGuild(slash, gid);
            count++;
        }
        _logger.LogInformation("Registered slash commands for {Count} active tenant(s).", count);
    }

    /// <summary>
    /// Registers commands for a guild admitted at runtime and refreshes Discord.
    /// Best-effort: if the live refresh fails, the commands still land on the
    /// next restart via <see cref="RegisterStartupCommandsAsync"/>.
    /// </summary>
    public async Task RegisterForGuildAsync(string guildId)
    {
        var slash = _slash;
        if (slash is null)
        {
            _logger.LogWarning("Tenant {GuildId} added before slash extension was attached; commands register on next restart.", guildId);
            return;
        }
        if (!ulong.TryParse(guildId, out var gid))
        {
            _logger.LogWarning("Cannot register commands for non-numeric guild id '{GuildId}'.", guildId);
            return;
        }

        RegisterModulesForGuild(slash, gid);

        try
        {
            await slash.RefreshCommands();
            _logger.LogInformation("Registered and refreshed slash commands for newly admitted guild {GuildId}.", gid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live slash-command refresh failed for guild {GuildId}; will register on next restart.", gid);
        }
    }

    private void RegisterModulesForGuild(SlashCommandsExtension slash, ulong guildId)
    {
        lock (_lock)
        {
            if (!_registeredGuilds.Add(guildId)) return; // already registered this session
        }

        slash.RegisterCommands<PlaybackCommands>(guildId);
        slash.RegisterCommands<QueueCommands>(guildId);
        slash.RegisterCommands<InfoCommands>(guildId);
        slash.RegisterCommands<DJCommands>(guildId);
        slash.RegisterCommands<ConfigCommands>(guildId);
        slash.RegisterCommands<AdminCommands>(guildId);
    }
}
