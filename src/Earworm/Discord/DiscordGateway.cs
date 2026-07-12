using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.EventArgs;

namespace Earworm.Discord;

/// <summary>
/// Owns the gateway lifecycle (connect / disconnect) and tracks ready state
/// for the /health endpoint. Readiness follows the WebSocket lifecycle: it is
/// asserted after Ready/Resumed and cleared after a close or missed-heartbeat
/// zombie event. Event subscriptions happen in the ctor, which means this
/// service must be resolved from DI before the gateway actually connects.
/// </summary>
public sealed class DiscordGateway
{
    private readonly DiscordClient _discordClient;
    private readonly ILogger<DiscordGateway> _logger;

    public DiscordClient Client => _discordClient;

    // IsReady is written from the DSharpPlus gateway thread and read from the
    // ASP.NET Core /health request thread. volatile guarantees the read sees
    // the latest write without a lock.
    private volatile bool _isReady;
    public bool IsReady => _isReady;

    public DiscordGateway(DiscordClient discordClient, ILogger<DiscordGateway> logger)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _discordClient.Ready += OnReadyAsync;
        _discordClient.Resumed += OnResumedAsync;
        _discordClient.SocketClosed += OnSocketClosedAsync;
        _discordClient.Zombied += OnZombiedAsync;
        _discordClient.GuildDownloadCompleted += OnGuildDownloadCompletedAsync;
    }

    private Task OnReadyAsync(DiscordClient sender, ReadyEventArgs e)
    {
        _isReady = true;
        _logger.LogInformation("Connected to Discord Gateway. Bot is ready.");
        return Task.CompletedTask;
    }

    private Task OnResumedAsync(DiscordClient sender, ReadyEventArgs e)
    {
        _isReady = true;
        _logger.LogInformation("Discord Gateway session resumed. Bot is ready.");
        return Task.CompletedTask;
    }

    private Task OnSocketClosedAsync(DiscordClient sender, SocketCloseEventArgs e)
    {
        _isReady = false;
        _logger.LogWarning(
            "Discord Gateway socket closed ({CloseCode}: {CloseMessage}); readiness cleared.",
            e.CloseCode,
            e.CloseMessage);
        return Task.CompletedTask;
    }

    private Task OnZombiedAsync(DiscordClient sender, ZombiedEventArgs e)
    {
        _isReady = false;
        _logger.LogWarning(
            "Discord Gateway missed {FailureCount} heartbeat acknowledgements; readiness cleared.",
            e.Failures);
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
        var connectTask = _discordClient.ConnectAsync();
        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
        var winner = await Task.WhenAny(connectTask, cancelTask);
        if (winner == cancelTask)
        {
            _isReady = false;
            _ = ObserveCancelledConnectAsync(connectTask);
            try { await _discordClient.DisconnectAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Discord disconnect during canceled startup was not yet available."); }
            cancellationToken.ThrowIfCancellationRequested();
        }
        await connectTask;
    }

    private async Task ObserveCancelledConnectAsync(Task connectTask)
    {
        try
        {
            await connectTask;
            // DSharpPlus has no cancellation token for ConnectAsync. If it
            // finishes after startup was canceled, close the late session.
            await _discordClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Canceled Discord startup completed with an expected late failure.");
        }
        finally
        {
            _isReady = false;
        }
    }

    public async Task StopAsync()
    {
        _isReady = false;
        _logger.LogInformation("Disconnecting from Discord Gateway...");
        await _discordClient.DisconnectAsync();
    }
}
