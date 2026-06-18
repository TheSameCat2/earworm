using System;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Earworm.Domain.Player;
using Earworm.Infrastructure;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord.Attributes;

/// <summary>
/// Command allowed if the caller has the DJ role, is an Administrator, or
/// originally queued the currently-playing track. Used by /remove on the
/// playing track.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequesterOrDjAttribute : SlashCheckBaseAttribute
{
    public override async Task<bool> ExecuteChecksAsync(InteractionContext ctx)
    {
        if (ctx.Member == null || ctx.Guild == null) return false;
        if (ctx.Member.Permissions.HasPermission(Permissions.Administrator)) return true;

        var settings = ctx.Services.GetRequiredService<ISettingsRepository>();
        var djRoleId = await settings.GetDjRoleIdAsync(ctx.Guild.Id.ToString());
        if (djRoleId.HasValue && ctx.Member.Roles.Any(r => r.Id == djRoleId.Value)) return true;

        var player = ctx.Services.GetRequiredService<PerGuildRegistry<PlayerEngine>>().GetOrCreate(ctx.Guild.Id.ToString());
        var state = player.State;
        return state != null && state.CurrentRequestedByUserId == ctx.User.Id.ToString();
    }
}
