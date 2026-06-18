using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Earworm.Domain.Tenants;

namespace Earworm.Discord.Attributes;

/// <summary>
/// Class-level gate applied to every user-facing command module: the command
/// only runs in a guild that has been admitted as a tenant (status 'active').
/// Admin commands deliberately do NOT carry this — a bot owner must be able to
/// whitelist the first server before any guild is admitted.
///
/// A failed check surfaces via the <c>SlashCommandErrored</c> handler wired in
/// Program.cs, which replies ephemerally rather than failing silently.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, AllowMultiple = false)]
public sealed class WhitelistedGuildAttribute : SlashCheckBaseAttribute
{
    public override async Task<bool> ExecuteChecksAsync(InteractionContext ctx)
    {
        if (ctx.Guild == null) return false; // commands are guild-only
        var tenants = ctx.Services.GetRequiredService<ITenantService>();
        return await tenants.IsAdmittedAsync(ctx.Guild.Id.ToString());
    }
}
