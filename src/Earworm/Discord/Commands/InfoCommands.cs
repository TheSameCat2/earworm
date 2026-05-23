using System;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Earworm.Config;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord.Commands;

public sealed class InfoCommands : ApplicationCommandModule
{
    private readonly IHistoryRepository _history;
    private readonly IMetricsRepository _metrics;
    private readonly EarwormConfig _config;

    public InfoCommands(IHistoryRepository history, IMetricsRepository metrics, EarwormConfig config)
    {
        _history = history;
        _metrics = metrics;
        _config = config;
    }

    [SlashCommand("history", "View recently played music tracks.")]
    public async Task ViewHistoryAsync(InteractionContext ctx,
        [Option("limit", "Number of tracks to show")] long limit = 20)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        int finalLimit = Math.Clamp((int)limit, 1, Math.Max(1, _config.Persistence.HistoryMaxN));
        var history = await _history.GetRecentHistoryAsync(finalLimit);

        var embed = new DiscordEmbedBuilder()
            .WithTitle("Recently Played Tracks 📜")
            .WithColor(new DiscordColor("#3498db"))
            .WithTimestamp(DateTimeOffset.UtcNow);

        if (history.Count > 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < history.Count; i++)
            {
                var entry = history[i];
                string durationStr = "Unknown";
                if (entry.DurationSeconds.HasValue)
                {
                    var ts = TimeSpan.FromSeconds(entry.DurationSeconds.Value);
                    durationStr = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
                }
                string timeAgo = GetRelativeTime(entry.PlayedAt);
                string reqStr = string.IsNullOrEmpty(entry.RequestedByUserId)
                    ? entry.RequestedByDisplayName ?? "System"
                    : $"<@{entry.RequestedByUserId}>";
                sb.AppendLine($"`{i + 1}.` **{entry.Title}** - *{entry.Artist ?? "Unknown"}* (`{durationStr}`) | {timeAgo} | Requested by {reqStr}");
            }
            embed.WithDescription(sb.ToString());
        }
        else
        {
            embed.WithDescription("*No tracks have been played yet. Play some tracks first!*");
        }

        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
    }

    [SlashCommand("stats", "View global bot statistics and the top listener leaderboard.")]
    public async Task ViewStatsAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        var globalMetrics = await _metrics.GetGlobalMetricsAsync();
        var topListeners = await _metrics.GetTopUsersByListeningTimeAsync(5);
        var topQueuers = await _metrics.GetTopUsersByTracksQueuedAsync(5);

        var embed = new DiscordEmbedBuilder()
            .WithTitle("earworm Bot Statistics & Leaderboard 📊")
            .WithColor(new DiscordColor("#e74c3c"))
            .WithTimestamp(DateTimeOffset.UtcNow);

        globalMetrics.TryGetValue("tracks_queued", out long tracksQueued);
        globalMetrics.TryGetValue("tracks_completed", out long tracksCompleted);
        globalMetrics.TryGetValue("listening_seconds", out long listeningSeconds);
        globalMetrics.TryGetValue("requests_youtube", out long reqYoutube);
        globalMetrics.TryGetValue("requests_soundcloud", out long reqSoundcloud);
        globalMetrics.TryGetValue("requests_mp3_upload", out long reqMp3);
        globalMetrics.TryGetValue("requests_search", out long reqSearch);

        var tsListening = TimeSpan.FromSeconds(listeningSeconds);
        string listeningTimeStr = $"{(int)tsListening.TotalHours}h {tsListening.Minutes}m";

        embed.AddField("Global Playback Metrics",
            $"Total Tracks Queued: `{tracksQueued}`\n" +
            $"Tracks Successfully Completed: `{tracksCompleted}`\n" +
            $"Total Listening Time: `{listeningTimeStr}`",
            inline: false);

        embed.AddField("Request Sources",
            $"YouTube Searches/URLs: `{reqYoutube}`\n" +
            $"SoundCloud URLs: `{reqSoundcloud}`\n" +
            $"Direct MP3 Uploads: `{reqMp3}`\n" +
            $"Keyword Text Searches: `{reqSearch}`",
            inline: false);

        if (topListeners.Count > 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < topListeners.Count; i++)
            {
                var user = topListeners[i];
                var ts = TimeSpan.FromSeconds(user.ListeningSeconds);
                string timeStr = $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
                sb.AppendLine($"`#{i + 1}` <@{user.UserId}> ({user.DisplayNameLastSeen}) - **{timeStr}**");
            }
            embed.AddField("Top Listeners by Time 🎧", sb.ToString(), inline: true);
        }
        else
        {
            embed.AddField("Top Listeners by Time 🎧", "*No listener metrics recorded yet.*", inline: true);
        }

        if (topQueuers.Count > 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < topQueuers.Count; i++)
            {
                var user = topQueuers[i];
                sb.AppendLine($"`#{i + 1}` <@{user.UserId}> ({user.DisplayNameLastSeen}) - **{user.TracksQueued}** tracks");
            }
            embed.AddField("Top Song Queuers 🎵", sb.ToString(), inline: true);
        }
        else
        {
            embed.AddField("Top Song Queuers 🎵", "*No queuing metrics recorded yet.*", inline: true);
        }

        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
    }

    private static string GetRelativeTime(DateTimeOffset dateTime)
    {
        var ts = DateTimeOffset.UtcNow - dateTime;
        if (ts.TotalSeconds < 60) return "just now";
        if (ts.TotalMinutes < 2) return "1 minute ago";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes} minutes ago";
        if (ts.TotalHours < 2) return "1 hour ago";
        if (ts.TotalHours < 24) return $"{(int)ts.TotalHours} hours ago";
        if (ts.TotalDays < 2) return "yesterday";
        return $"{(int)ts.TotalDays} days ago";
    }
}
