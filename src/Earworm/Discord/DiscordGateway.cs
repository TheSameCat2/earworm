using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.EventArgs;

namespace Earworm.Discord;

/// <summary>
/// Owns the gateway lifecycle (connect / disconnect) and tracks ready state
/// for the /health endpoint. Subscribes to Ready + GuildDownloadCompleted in
/// the ctor, which means it must be resolved from DI before the gateway
/// actually connects.
/// </summary>
public sealed class DiscordGateway
{
    private readonly DiscordClient _discordClient;
    private readonly ILogger<DiscordGateway> _logger;

    public DiscordClient Client => _discordClient;
    public bool IsReady { get; private set; }

    public DiscordGateway(DiscordClient discordClient, ILogger<DiscordGateway> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _discordClient.Ready += OnReadyAsync;
        _discordClient.GuildDownloadCompleted += OnGuildDownloadCompletedAsync;
    }

    private Task OnReadyAsync(DiscordClient sender, ReadyEventArgs e)
    {
        IsReady = true;
        _logger.LogInformation("Connected to Discord Gateway. Bot is ready.");
        return Task.CompletedTask;
    }

    private Task OnGuildDownloadCompletedAsync(DiscordClient sender, GuildDownloadCompletedEventArgs e)
    {
        _logger.LogInformation("Synchronized with {GuildCount} guilds.", e.Guilds.Count);
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Connecting to Discord Gateway...");
        await _discordClient.ConnectAsync();
    }

    public async Task StopAsync()
    {
        IsReady = false;
        _logger.LogInformation("Disconnecting from Discord Gateway...");
        await _discordClient.DisconnectAsync();
    }
}
