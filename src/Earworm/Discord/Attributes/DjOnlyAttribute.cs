using System;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord.Attributes;

/// <summary>
/// PRD §4: DJ role required for destructive commands (/skip, /stop-worm, etc.).
/// Administrators always bypass.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class DjOnlyAttribute : SlashCheckBaseAttribute
{
    public override async Task<bool> ExecuteChecksAsync(InteractionContext ctx)
    {
        if (ctx.Member == null) return false;
        if (ctx.Member.Permissions.HasPermission(Permissions.Administrator)) return true;

        var settings = ctx.Services.GetRequiredService<ISettingsRepository>();
        var djRoleId = await settings.GetDjRoleIdAsync();
        return djRoleId.HasValue && ctx.Member.Roles.Any(r => r.Id == djRoleId.Value);
    }
}
