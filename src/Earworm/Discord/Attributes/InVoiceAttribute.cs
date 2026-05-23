using System;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;

namespace Earworm.Discord.Attributes;

/// <summary>
/// Requires the caller to be in the same voice channel as the bot (or any
/// voice channel, if the bot isn't connected yet).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class InVoiceAttribute : SlashCheckBaseAttribute
{
    public override Task<bool> ExecuteChecksAsync(InteractionContext ctx)
    {
        if (ctx.Member == null) return Task.FromResult(false);

        var memberChannelId = ctx.Member.VoiceState?.Channel?.Id;
        if (memberChannelId == null) return Task.FromResult(false);

        var botChannelId = ctx.Guild?.CurrentMember?.VoiceState?.Channel?.Id;
        if (botChannelId != null && botChannelId != memberChannelId) return Task.FromResult(false);

        return Task.FromResult(true);
    }
}
