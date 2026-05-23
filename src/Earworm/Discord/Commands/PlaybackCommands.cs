using System;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Earworm.Discord.Attributes;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord.Commands;

public sealed class PlaybackCommands : ApplicationCommandModule
{
    private readonly VoiceManager _voice;
    private readonly PlayerEngine _player;
    private readonly QueueManager _queue;
    private readonly TrackQueuingService _queuing;
    private readonly ISettingsRepository _settings;

    public PlaybackCommands(
        VoiceManager voice,
        PlayerEngine player,
        QueueManager queue,
        TrackQueuingService queuing,
        ISettingsRepository settings)
    {
        _voice = voice;
        _player = player;
        _queue = queue;
        _queuing = queuing;
        _settings = settings;
    }

    private static DiscordWebhookBuilder Text(string s) =>
        new DiscordWebhookBuilder().WithContent(s);

    [SlashCommand("play", "Queue a track by URL or search query. Equivalent to mentioning the bot.")]
    public async Task PlayAsync(InteractionContext ctx,
        [Option("query", "YouTube/SoundCloud URL, or a search query")] string query)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        var voiceChannel = ctx.Member?.VoiceState?.Channel;
        if (voiceChannel == null)
        {
            await ctx.EditResponseAsync(Text("⚠️ You must be in a voice channel to queue music."));
            return;
        }

        if (_queuing.IsPlaylistUrl(query))
        {
            var djRoleId = await _settings.GetDjRoleIdAsync();
            bool isDj = ctx.Member!.Permissions.HasPermission(Permissions.Administrator)
                || (djRoleId.HasValue && ctx.Member.Roles.Any(r => r.Id == djRoleId.Value));
            if (!isDj)
            {
                await ctx.EditResponseAsync(Text("⚠️ Playlist URLs are restricted to DJs (a single paste can flood the queue)."));
                return;
            }
        }

        try
        {
            var item = await _queuing.ResolveAndQueueAsync(
                query,
                ctx.User.Id.ToString(),
                ctx.Member!.DisplayName,
                ctx.Guild!.Id.ToString());

            var botChannelId = ctx.Guild.CurrentMember?.VoiceState?.Channel?.Id;
            if (botChannelId == null)
            {
                await _voice.JoinChannelAsync(voiceChannel);
            }

            string label = string.IsNullOrEmpty(item.Title) ? "track" : $"**{item.Title}**";
            await ctx.EditResponseAsync(Text($"🎵 Queued {label}."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Couldn't queue that: {ex.Message}"));
        }
    }

    [SlashCommand("start-worm", "Connect the bot to a voice channel and begin playback.")]
    public async Task StartWormAsync(InteractionContext ctx,
        [Option("channel", "The voice channel to join (defaults to your current channel)")] DiscordChannel? channel = null)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

        var targetChannel = channel ?? ctx.Member?.VoiceState?.Channel;
        if (targetChannel == null)
        {
            await ctx.EditResponseAsync(Text("⚠️ You must be in a voice channel or specify a channel to start the bot."));
            return;
        }

        if (targetChannel.Type != ChannelType.Voice && targetChannel.Type != ChannelType.Stage)
        {
            await ctx.EditResponseAsync(Text("⚠️ Specified channel must be a voice or stage channel."));
            return;
        }

        try
        {
            await _voice.JoinChannelAsync(targetChannel);
            await ctx.EditResponseAsync(Text($"🎵 Joined voice channel: **{targetChannel.Name}** and initiated playback!"));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to join voice channel: {ex.Message}"));
        }
    }

    [SlashCommand("stop-worm", "Stop playback and disconnect the bot from voice."), DjOnly]
    public async Task StopWormAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            await _voice.LeaveChannelAsync(ctx.Guild!.Id);
            await ctx.EditResponseAsync(Text("🛑 Stopped playback and disconnected from voice."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to stop playback: {ex.Message}"));
        }
    }

    [SlashCommand("pause", "Pause current audio playback."), InVoice, DjOnly]
    public async Task PauseAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try { await _player.PauseAsync(); await ctx.EditResponseAsync(Text("⏸️ Playback paused.")); }
        catch (Exception ex) { await ctx.EditResponseAsync(Text($"⚠️ Failed to pause: {ex.Message}")); }
    }

    [SlashCommand("resume", "Resume paused audio playback."), InVoice, DjOnly]
    public async Task ResumeAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try { await _player.ResumeAsync(); await ctx.EditResponseAsync(Text("▶️ Playback resumed.")); }
        catch (Exception ex) { await ctx.EditResponseAsync(Text($"⚠️ Failed to resume: {ex.Message}")); }
    }

    [SlashCommand("skip", "Skip the currently playing track."), InVoice, DjOnly]
    public async Task SkipAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try { await _player.SkipAsync(); await ctx.EditResponseAsync(Text("⏭️ Track skipped.")); }
        catch (Exception ex) { await ctx.EditResponseAsync(Text($"⚠️ Failed to skip: {ex.Message}")); }
    }

    [SlashCommand("previous", "Play the previously played track."), InVoice, DjOnly]
    public async Task PreviousAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try { await _player.PreviousAsync(); await ctx.EditResponseAsync(Text("⏮️ Playing previous track.")); }
        catch (Exception ex) { await ctx.EditResponseAsync(Text($"⚠️ Failed to go to previous track: {ex.Message}")); }
    }

    [SlashCommand("seek", "Seek to a specific position in the current track (mm:ss or seconds)."), InVoice, DjOnly]
    public async Task SeekAsync(InteractionContext ctx,
        [Option("position", "Time position (format: mm:ss or seconds)")] string positionStr)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try
        {
            TimeSpan seekTime;
            if (positionStr.Contains(':'))
            {
                var parts = positionStr.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int min) && int.TryParse(parts[1], out int sec))
                {
                    seekTime = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
                }
                else if (parts.Length == 3 && int.TryParse(parts[0], out int hr) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int s))
                {
                    seekTime = TimeSpan.FromHours(hr) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                }
                else
                {
                    await ctx.EditResponseAsync(Text("⚠️ Invalid time format. Please use `mm:ss` or total seconds (e.g. `150`)."));
                    return;
                }
            }
            else if (int.TryParse(positionStr, out int seconds))
            {
                seekTime = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                await ctx.EditResponseAsync(Text("⚠️ Invalid time format. Please use `mm:ss` or total seconds (e.g. `150`)."));
                return;
            }

            await _player.SeekAsync(seekTime);
            await ctx.EditResponseAsync(Text($"⏩ Seeked to **{positionStr}**."));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(Text($"⚠️ Failed to seek: {ex.Message}"));
        }
    }

    [SlashCommand("clear-worm", "Clear the entire music queue."), DjOnly]
    public async Task ClearQueueAsync(InteractionContext ctx)
    {
        await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
        try { await _queue.ClearQueueAsync(); await ctx.EditResponseAsync(Text("🧹 The music queue has been cleared.")); }
        catch (Exception ex) { await ctx.EditResponseAsync(Text($"⚠️ Failed to clear queue: {ex.Message}")); }
    }
}
