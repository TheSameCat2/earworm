using System;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Earworm.Discord.Attributes;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord.Commands;

[WhitelistedGuild]
public sealed class DJCommands : ApplicationCommandModule
{
    private readonly ISettingsRepository _settings;

    public DJCommands(ISettingsRepository settings) => _settings = settings;

    private static DiscordWebhookBuilder Text(string s) =>
        new DiscordWebhookBuilder().WithContent(s);

    [SlashCommand("djon", "Enable the AI DJ commentary engine."), DjOnly]
    public async Task EnableDjAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            await _settings.SetDjEnabledAsync(ctx.Guild!.Id.ToString(), true);
            await ctx.EditResponseAsync(Text("📻 **AI DJ commentary is now ENABLED!** The DJ will inject warm west-coast radio commentary over song transitions."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to enable AI DJ: {ex.Message}"));
        }
    }

    [SlashCommand("djoff", "Disable the AI DJ commentary engine."), DjOnly]
    public async Task DisableDjAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            await _settings.SetDjEnabledAsync(ctx.Guild!.Id.ToString(), false);
            await ctx.EditResponseAsync(Text("📻 **AI DJ commentary is now DISABLED.** Transitions will be clean without radio intros."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to disable AI DJ: {ex.Message}"));
        }
    }
}
