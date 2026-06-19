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
        var userId = ctx.User.Id.ToString();
        for (int i = 0; i < config.Bot.OwnerUserIds.Count; i++)
        {
            if (string.Equals(config.Bot.OwnerUserIds[i]?.Trim(), userId, StringComparison.Ordinal))
            {
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }
}
