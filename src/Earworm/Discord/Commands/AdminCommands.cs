using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Earworm.Discord.Attributes;
using Earworm.Domain.Tenants;

namespace Earworm.Discord.Commands;

[SlashCommandGroup("admin", "Bot owner administrative commands.")]
[OwnerOnly]
public sealed class AdminCommands : ApplicationCommandModule
{
    private readonly ITenantService _tenantService;
    private readonly TenantLifecycleListener _lifecycleListener;

    public AdminCommands(ITenantService tenantService, TenantLifecycleListener lifecycleListener)
    {
        _tenantService = tenantService;
        _lifecycleListener = lifecycleListener;
    }

    private static DiscordWebhookBuilder Text(string s) =>
        new DiscordWebhookBuilder().WithContent(s);

    [SlashCommand("add-server", "Whitelist a Discord server as a tenant.")]
    public async Task AddServerAsync(
        InteractionContext ctx,
        [Option("guild-id", "The Discord guild ID to whitelist")] string guildId)
    {
        await ctx.CreateResponseAsync(
            InteractionResponseType.DeferredChannelMessageWithSource,
            new DiscordInteractionResponseBuilder { IsEphemeral = true });
        if (!ulong.TryParse(guildId, out _))
        {
            await ctx.EditResponseAsync(Text($"Invalid guild ID `{guildId}` — must be a numeric Discord snowflake."));
            return;
        }

        try
        {
            await _tenantService.AddTenantAsync(guildId, ctx.User.Id.ToString());
            await _lifecycleListener.OnTenantAddedAsync(guildId);
            await ctx.EditResponseAsync(Text($"Server `{guildId}` has been added as a tenant."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"Failed to add server: {ex.Message}"));
        }
    }

    [SlashCommand("list-servers", "List all whitelisted tenant servers.")]
    public async Task ListServersAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(
            InteractionResponseType.DeferredChannelMessageWithSource,
            new DiscordInteractionResponseBuilder { IsEphemeral = true });
        try
        {
            var tenants = await _tenantService.GetAllTenantsAsync();
            if (tenants.Count == 0)
            {
                await ctx.EditResponseAsync(Text("No tenant servers registered."));
                return;
            }

            var sb = new StringBuilder();
            foreach (var t in tenants)
            {
                sb.AppendLine($"• `{t.GuildId}` — plan: **{t.Plan}**, status: **{t.Status}**" +
                    (t.OwnerUserId is not null ? $", owner: `{t.OwnerUserId}`" : string.Empty));
            }

            var embed = new DiscordEmbedBuilder()
                .WithTitle($"Tenant Servers ({tenants.Count})")
                .WithColor(new DiscordColor("#5865F2"))
                .WithDescription(sb.ToString())
                .WithTimestamp(DateTimeOffset.UtcNow);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"Failed to list servers: {ex.Message}"));
        }
    }

    [SlashCommand("remove-server", "Remove a Discord server from the tenant whitelist.")]
    public async Task RemoveServerAsync(
        InteractionContext ctx,
        [Option("guild-id", "The Discord guild ID to remove")] string guildId)
    {
        await ctx.CreateResponseAsync(
            InteractionResponseType.DeferredChannelMessageWithSource,
            new DiscordInteractionResponseBuilder { IsEphemeral = true });
        if (!ulong.TryParse(guildId, out _))
        {
            await ctx.EditResponseAsync(Text($"Invalid guild ID `{guildId}` — must be a numeric Discord snowflake."));
            return;
        }

        try
        {
            await _tenantService.RemoveTenantAsync(guildId);
            await _lifecycleListener.OnTenantRemovedAsync(guildId);
            await ctx.EditResponseAsync(Text($"Server `{guildId}` has been suspended and will no longer have access to bot commands."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"Failed to remove server: {ex.Message}"));
        }
    }
}
