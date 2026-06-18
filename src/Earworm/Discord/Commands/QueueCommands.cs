using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Earworm.Discord.Attributes;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Infrastructure;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord.Commands;

[WhitelistedGuild]
public sealed class QueueCommands : ApplicationCommandModule
{
    private readonly PerGuildRegistry<QueueManager> _queues;
    private readonly PerGuildRegistry<PlayerEngine> _players;
    private readonly VoiceManager _voice;
    private readonly ISettingsRepository _settings;

    public QueueCommands(
        PerGuildRegistry<QueueManager> queues,
        PerGuildRegistry<PlayerEngine> players,
        VoiceManager voice,
        ISettingsRepository settings)
    {
        _queues = queues;
        _players = players;
        _voice = voice;
        _settings = settings;
    }

    private static DiscordWebhookBuilder Text(string s) =>
        new DiscordWebhookBuilder().WithContent(s);

    [SlashCommand("queue", "View the currently playing track and the upcoming queue.")]
    public async Task ViewQueueAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        var gid = ctx.Guild!.Id.ToString();
        var queue = _queues.GetOrCreate(gid).GetQueue();
        var state = _players.GetOrCreate(gid).State;

        var embed = new DiscordEmbedBuilder()
            .WithTitle("Music Queue 🎶")
            .WithColor(new DiscordColor("#1DB954"))
            .WithTimestamp(DateTimeOffset.UtcNow);

        if (state != null && state.IsPlaying && !string.IsNullOrEmpty(state.CurrentTitle))
        {
            string durationStr = "Unknown";
            if (state.CurrentDurationSeconds.HasValue)
            {
                var ts = TimeSpan.FromSeconds(state.CurrentDurationSeconds.Value);
                durationStr = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
            }

            string positionStr = "0:00";
            if (state.CurrentPositionMs > 0)
            {
                var ts = TimeSpan.FromMilliseconds(state.CurrentPositionMs);
                positionStr = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
            }

            string requesterStr = string.IsNullOrEmpty(state.CurrentRequestedByUserId)
                ? state.CurrentRequestedByDisplayName ?? "System"
                : $"<@{state.CurrentRequestedByUserId}>";

            embed.AddField("Now Playing",
                $"**{state.CurrentTitle}**\n" +
                $"Artist: *{state.CurrentArtist ?? "Unknown"}*\n" +
                $"Position: `{positionStr} / {durationStr}`\n" +
                $"Requested By: {requesterStr}",
                inline: false);
        }
        else
        {
            embed.AddField("Now Playing", "*Nothing is currently playing. Use `/start-worm` or mention the bot to play!*", inline: false);
        }

        if (queue.Count > 0)
        {
            var sb = new StringBuilder();
            int countToShow = Math.Min(queue.Count, 10);
            for (int i = 0; i < countToShow; i++)
            {
                var item = queue[i];
                string durationStr = "Unknown";
                if (item.DurationSeconds.HasValue)
                {
                    var ts = TimeSpan.FromSeconds(item.DurationSeconds.Value);
                    durationStr = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
                }
                string reqStr = string.IsNullOrEmpty(item.RequestedByUserId)
                    ? item.RequestedByDisplayName
                    : $"<@{item.RequestedByUserId}>";
                sb.AppendLine($"`{i + 1}.` **{item.Title}** - *{item.Artist ?? "Unknown"}* (`{durationStr}`) | Requested by {reqStr}");
            }
            if (queue.Count > 10) sb.AppendLine($"\n*... and {queue.Count - 10} more tracks in the queue.*");
            embed.AddField("Next Up", sb.ToString(), inline: false);
        }
        else
        {
            embed.AddField("Next Up", "*Queue is empty.*", inline: false);
        }

        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
    }

    [SlashCommand("remove", "Remove a track from the queue at a specific position."), InVoice]
    public async Task RemoveTrackAsync(InteractionContext ctx,
        [Option("position", "The position in the queue (e.g. 1 for the first upcoming track)")] long position)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        if (position <= 0)
        {
            await ctx.EditResponseAsync(Text("⚠️ Position must be a positive integer greater than 0."));
            return;
        }

        var gid = ctx.Guild!.Id.ToString();
        var queueManager = _queues.GetOrCreate(gid);
        int index = (int)position - 1;
        int queueCount = queueManager.Count;
        if (index >= queueCount)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Invalid position. The queue currently only has {queueCount} tracks."));
            return;
        }

        try
        {
            var djRoleId = await _settings.GetDjRoleIdAsync(gid);
            bool isDj = ctx.Member!.Permissions.HasPermission(Permissions.Administrator)
                || (djRoleId.HasValue && ctx.Member.Roles.Any(r => r.Id == djRoleId.Value));

            var removedTrack = await queueManager.RemoveTrackAsync(index, ctx.User.Id.ToString(), isDj);
            await ctx.EditResponseAsync(Text($"❌ Removed track **{removedTrack.Title}** from position `{position}` in the queue."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to remove track: {ex.Message}"));
        }
    }

    [SlashCommand("move", "Move a track's position in the queue."), DjOnly]
    public async Task MoveTrackAsync(InteractionContext ctx,
        [Option("from", "The track's current position in the queue")] long from,
        [Option("to", "The new position in the queue for the track")] long to)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        var queueManager = _queues.GetOrCreate(ctx.Guild!.Id.ToString());
        var queue = queueManager.GetQueue();
        if (from <= 0 || from > queue.Count || to <= 0 || to > queue.Count)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Positions must be between 1 and {queue.Count}."));
            return;
        }

        int fromIndex = (int)from - 1;
        int toIndex = (int)to - 1;

        try
        {
            var track = queue[fromIndex];
            await queueManager.MoveTrackAsync(fromIndex, toIndex);
            await ctx.EditResponseAsync(Text($"🔄 Moved track **{track.Title}** from position `{from}` to `{to}`."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to move track: {ex.Message}"));
        }
    }

    [SlashCommand("save", "Capture and save a snapshot of the current queue."), DjOnly]
    public async Task SaveSnapshotAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            await _queues.GetOrCreate(ctx.Guild!.Id.ToString()).SaveSnapshotAsync(ctx.User.Id.ToString());
            await ctx.EditResponseAsync(Text("💾 Saved a snapshot of the current queue. Use `/restore` to reload it at any time!"));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to save snapshot: {ex.Message}"));
        }
    }

    [SlashCommand("restore", "Restore the previously saved queue snapshot."), DjOnly]
    public async Task RestoreSnapshotAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            var restoredState = await _queues.GetOrCreate(ctx.Guild!.Id.ToString()).RestoreSnapshotAsync();
            if (restoredState == null)
            {
                await ctx.EditResponseAsync(Text("📂 No saved snapshot to restore."));
                return;
            }

            var botChannel = ctx.Guild!.CurrentMember?.VoiceState?.Channel;
            if (botChannel == null)
            {
                var memberChannel = ctx.Member?.VoiceState?.Channel;
                if (memberChannel == null)
                {
                    await ctx.EditResponseAsync(Text("📂 Snapshot restored, but you're not in a voice channel — join one and run /start-worm to begin playback."));
                    return;
                }
                await _voice.JoinChannelAsync(memberChannel);
            }

            await ctx.EditResponseAsync(Text("📂 Snapshot restored. Resuming playback."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to restore snapshot: {ex.Message}"));
        }
    }
}
