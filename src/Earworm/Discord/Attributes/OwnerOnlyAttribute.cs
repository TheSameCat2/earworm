using System;
using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Earworm.Config;

namespace Earworm.Discord.Attributes;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class OwnerOnlyAttribute : SlashCheckBaseAttribute
{
    public override Task<bool> ExecuteChecksAsync(InteractionContext ctx)
    {
        var config = ctx.Services.GetRequiredService<EarwormConfig>();
        return Task.FromResult(config.Bot.OwnerUserIds.Contains(ctx.User.Id.ToString()));
    }
}
