using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Earworm.Discord;
using Earworm.Discord.Attributes;
using Earworm.Domain.Tenants;

namespace Earworm.Discord.Commands;

[SlashCommandGroup("admin", "Bot owner administrative commands.")]
[OwnerOnly]
public sealed class AdminCommands : ApplicationCommandModule
{
    private const int MaxEmbedDescriptionLength = 4096;
    private const string TenantListTruncatedNotice = "*Additional tenant servers were omitted to fit Discord's message limit.*";

    private readonly ITenantService _tenantService;
    private readonly TenantLifecycleListener _lifecycle;

    public AdminCommands(ITenantService tenantService, TenantLifecycleListener lifecycle)
    {
        _tenantService = tenantService;
        _lifecycle = lifecycle;
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
        if (!DiscordGuildId.TryNormalize(guildId, out var canonicalGuildId))
        {
            await ctx.EditResponseAsync(Text($"Invalid guild ID `{guildId}` — must be a numeric Discord snowflake."));
            return;
        }

        try
        {
            await _lifecycle.AdmitGuildAsync(canonicalGuildId, ctx.User.Id.ToString());
            await ctx.EditResponseAsync(Text($"Server `{canonicalGuildId}` has been added as a tenant. Slash commands are being registered for it now."));
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
            for (int i = 0; i < tenants.Count; i++)
            {
                var t = tenants[i];
                string line = $"• `{t.GuildId}` — plan: **{t.Plan}**, status: **{t.Status}**" +
                    (t.OwnerUserId is not null ? $", owner: `{t.OwnerUserId}`" : string.Empty);
                bool hasMore = i < tenants.Count - 1;
                int noticeReserve = hasMore
                    ? TenantListTruncatedNotice.Length + Environment.NewLine.Length
                    : 0;
                int availableForLine = MaxEmbedDescriptionLength
                    - sb.Length
                    - Environment.NewLine.Length
                    - noticeReserve;
                if (availableForLine <= 0)
                {
                    sb.Append(TenantListTruncatedNotice);
                    break;
                }
                if (line.Length > availableForLine)
                {
                    sb.AppendLine(TruncateWithEllipsis(line, availableForLine));
                    if (hasMore) sb.Append(TenantListTruncatedNotice);
                    break;
                }
                sb.AppendLine(line);
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
        if (!DiscordGuildId.TryNormalize(guildId, out var canonicalGuildId))
        {
            await ctx.EditResponseAsync(Text($"Invalid guild ID `{guildId}` — must be a numeric Discord snowflake."));
            return;
        }

        try
        {
            await _lifecycle.SuspendGuildAsync(canonicalGuildId);
            await ctx.EditResponseAsync(Text($"Server `{canonicalGuildId}` has been suspended and will no longer have access to bot commands. Its data has been retained for a future re-admit."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"Failed to remove server: {ex.Message}"));
        }
    }

    private static string TruncateWithEllipsis(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        if (maxLength <= 0) return string.Empty;
        if (maxLength == 1) return "…";
        return value[..(maxLength - 1)] + "…";
    }
}
