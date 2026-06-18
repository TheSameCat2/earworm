using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Earworm.Domain.Queue;
using Earworm.Domain.Tenants;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord;

public sealed class MessageListener : IDisposable
{
    private static readonly DiscordEmoji EmojiX = DiscordEmoji.FromUnicode("❌");
    private static readonly DiscordEmoji EmojiHourglass = DiscordEmoji.FromUnicode("⏳");
    private static readonly DiscordEmoji EmojiCheck = DiscordEmoji.FromUnicode("✅");

    private readonly DiscordClient _discordClient;
    private readonly VoiceManager _voiceManager;
    private readonly TrackQueuingService _trackQueuingService;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ITenantService _tenantService;
    private readonly ILogger<MessageListener> _logger;
    private readonly ShutdownLifetime _shutdown;
    private Regex? _mentionRegex;

    public MessageListener(
        DiscordClient discordClient,
        VoiceManager voiceManager,
        TrackQueuingService trackQueuingService,
        ISettingsRepository settingsRepository,
        ITenantService tenantService,
        ILogger<MessageListener> logger,
        ShutdownLifetime shutdown)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _voiceManager = voiceManager ?? throw new ArgumentNullException(nameof(voiceManager));
        _trackQueuingService = trackQueuingService ?? throw new ArgumentNullException(nameof(trackQueuingService));
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _tenantService = tenantService ?? throw new ArgumentNullException(nameof(tenantService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));

        _discordClient.MessageCreated += OnMessageCreatedAsync;
    }

    private Task OnMessageCreatedAsync(DiscordClient sender, MessageCreateEventArgs e)
    {
        if (e.Author.IsBot || e.Guild == null) return Task.CompletedTask;
        if (!e.MentionedUsers.Any(u => u.Id == sender.CurrentUser.Id)) return Task.CompletedTask;

        var ct = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // Tenant gate: only serve @mentions in whitelisted guilds. Stay
                // silent in non-admitted servers rather than spamming a refusal.
                if (!await _tenantService.IsAdmittedAsync(e.Guild.Id.ToString()))
                {
                    _logger.LogInformation("Ignoring mention in non-admitted guild {GuildId}.", e.Guild.Id);
                    return;
                }

                var member = await e.Guild.GetMemberAsync(e.Author.Id);
                if (member == null) return;

                var voiceChannel = member.VoiceState?.Channel;
                if (voiceChannel == null)
                {
                    await e.Message.CreateReactionAsync(EmojiX);
                    await e.Message.RespondAsync("⚠️ You must be in a voice channel to queue music.");
                    return;
                }

                // Extract query (strip bot mention).
                var content = e.Message.Content;
                _mentionRegex ??= new Regex($"<@!?{sender.CurrentUser.Id}>", RegexOptions.Compiled);
                var query = _mentionRegex.Replace(content, "").Trim();

                // Prefer attachment URL if any audio file is attached. Lavalink can
                // load Discord attachment URLs directly via the HTTP source.
                var attachment = e.Message.Attachments.FirstOrDefault(a =>
                    a.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                    a.FileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                    a.FileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                    a.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                    a.FileName.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
                if (attachment != null)
                {
                    query = attachment.Url;
                }

                if (string.IsNullOrWhiteSpace(query))
                {
                    await e.Message.RespondAsync("🎶 Hello! Mention me with a song name, YouTube/SoundCloud link, or upload an audio file to queue it.");
                    return;
                }

                // Playlist gating (DJ only).
                if (_trackQueuingService.IsPlaylistUrl(query))
                {
                    var djRoleId = await _settingsRepository.GetDjRoleIdAsync(e.Guild.Id.ToString());
                    bool isDj = member.Permissions.HasPermission(Permissions.Administrator)
                        || (djRoleId.HasValue && member.Roles.Any(r => r.Id == djRoleId.Value));
                    if (!isDj)
                    {
                        await e.Message.CreateReactionAsync(EmojiX);
                        await e.Message.RespondAsync("⚠️ Playlist queuing is restricted to DJs or Administrators.");
                        return;
                    }
                }

                await e.Message.CreateReactionAsync(EmojiHourglass);

                _logger.LogInformation("Resolving query/URL '{Query}' from user {User}", query, e.Author.Username);
                await _trackQueuingService.ResolveAndQueueAsync(
                    query,
                    member.Id.ToString(),
                    member.DisplayName,
                    e.Guild.Id.ToString());

                await e.Message.DeleteOwnReactionAsync(EmojiHourglass);
                await e.Message.CreateReactionAsync(EmojiCheck);

                // Auto-join voice if not connected.
                var botChannelId = e.Guild.CurrentMember?.VoiceState?.Channel?.Id;
                if (botChannelId == null)
                {
                    _logger.LogInformation("Bot not in voice; auto-joining {ChannelName}", voiceChannel.Name);
                    await _voiceManager.JoinChannelAsync(voiceChannel);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: swallow.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling music request from message.");
                try
                {
                    await e.Message.DeleteOwnReactionAsync(EmojiHourglass);
                    await e.Message.CreateReactionAsync(EmojiX);
                    await e.Message.RespondAsync($"⚠️ Failed to queue song: {ex.Message}");
                }
                catch (Exception inner)
                {
                    _logger.LogError(inner, "Could not send failure reaction/response.");
                }
            }
        }, ct);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _discordClient.MessageCreated -= OnMessageCreatedAsync;
    }
}
