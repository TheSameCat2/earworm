using System;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Earworm.Discord.Attributes;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord.Commands;

[SlashCommandGroup("config", "Configure earworm bot settings.")]
public sealed class ConfigCommands : ApplicationCommandModule
{
    private readonly ISettingsRepository _settings;

    public ConfigCommands(ISettingsRepository settings) => _settings = settings;

    private static DiscordWebhookBuilder Text(string s) =>
        new DiscordWebhookBuilder().WithContent(s);

    [SlashCommand("dj-role", "Set the Discord role authorized to perform DJ actions."), ManageRolesOrAdmin]
    public async Task SetDjRoleAsync(InteractionContext ctx,
        [Option("role", "The role to authorize as DJ")] DiscordRole role)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            await _settings.SetDjRoleIdAsync(role.Id);
            await ctx.EditResponseAsync(Text($"⚙️ Authorized DJ role has been set to: **{role.Name}** (`{role.Id}`). Only users with this role (or Administrators) can execute DJ actions now."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to set DJ role: {ex.Message}"));
        }
    }

    [SlashCommand("logging-channel", "Set the text channel for bot system alerts and announcements."), DjOnly]
    public async Task SetLoggingChannelAsync(InteractionContext ctx,
        [Option("channel", "The text channel to send logs to")] DiscordChannel channel)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        if (channel.Type != ChannelType.Text)
        {
            await ctx.EditResponseAsync(Text("⚠️ The logging channel must be a text channel."));
            return;
        }

        try
        {
            await _settings.SetLoggingChannelIdAsync(channel.Id);
            await ctx.EditResponseAsync(Text($"⚙️ System logging channel has been set to: <#{channel.Id}> (`{channel.Id}`)."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to set logging channel: {ex.Message}"));
        }
    }

    [SlashCommand("show", "Display the currently active bot settings.")]
    public async Task ShowConfigAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            bool djEnabled = await _settings.IsDjEnabledAsync();
            var djRoleId = await _settings.GetDjRoleIdAsync();
            var loggingChannelId = await _settings.GetLoggingChannelIdAsync();

            string djRoleStr = djRoleId.HasValue ? $"<@&{djRoleId.Value}> (`{djRoleId.Value}`)" : "*None configured (Admins only)*";
            string loggingChanStr = loggingChannelId.HasValue ? $"<#{loggingChannelId.Value}> (`{loggingChannelId.Value}`)" : "*None configured*";

            var embed = new DiscordEmbedBuilder()
                .WithTitle("earworm Active Settings ⚙️")
                .WithColor(new DiscordColor("#f1c40f"))
                .AddField("AI DJ Commentary", djEnabled ? "📻 **Enabled**" : "📻 **Disabled**", inline: false)
                .AddField("Authorized DJ Role", djRoleStr, inline: false)
                .AddField("System Logging Channel", loggingChanStr, inline: false)
                .WithTimestamp(DateTimeOffset.UtcNow);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to display settings: {ex.Message}"));
        }
    }
}
