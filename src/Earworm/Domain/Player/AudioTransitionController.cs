using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lavalink4NET.Players;
using Earworm.Config;

namespace Earworm.Domain.Player;

/// <summary>
/// Volume ramps applied at music-track boundaries: fade-in when a music track
/// starts and fade-out in its final seconds. Both behaviours are driven from
/// a single position-polling loop per track so pause / resume / seek don't
/// need explicit handling — paused → position doesn't advance → fade math
/// stays put.
///
/// Tier-1 "crossfade" is really a tail-fade plus head-fade with a brief
/// (~100-500 ms) silent gap while Lavalink loads the next track; true overlap
/// isn't possible with a single LavalinkPlayer per guild.
///
/// Tier-1 "DJ ducking" reuses the same machinery at preroll boundaries: the
/// music fades out before the TTS, the TTS plays at full volume, then the next
/// music track fades in. The TTS itself isn't ramped because clarity matters.
///
/// When crossfade is disabled (<see cref="AudioConfig.CrossfadeSeconds"/> ≤ 0)
/// or the track is shorter than <see cref="AudioConfig.CrossfadeMinTrackSeconds"/>,
/// the controller leaves player volume untouched so disabling crossfade truly
/// means "don't touch volume" (forward-compatible with a future /volume).
/// </summary>
public sealed class AudioTransitionController
{
    private const float FullVolume = 1.0f;
    private const float Silent = 0.0f;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly EarwormConfig _config;
    private readonly ILogger<AudioTransitionController> _logger;

    private readonly object _lock = new();
    private CancellationTokenSource? _currentLoopCts;

    public AudioTransitionController(EarwormConfig config, ILogger<AudioTransitionController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private int FadeSeconds => Math.Max(0, _config.Audio.CrossfadeSeconds);
    private int MinTrackSeconds => Math.Max(0, _config.Audio.CrossfadeMinTrackSeconds);
    private bool IsEnabled => FadeSeconds > 0;

    public void Cancel()
    {
        CancellationTokenSource? toDispose;
        lock (_lock)
        {
            toDispose = _currentLoopCts;
            _currentLoopCts = null;
        }
        if (toDispose != null)
        {
            try { toDispose.Cancel(); } catch (ObjectDisposedException) { }
            toDispose.Dispose();
        }
    }

    /// <summary>
    /// Restore volume to full ahead of a TTS preroll, since the prior music
    /// track may have faded to silence. No-op when crossfade is disabled.
    /// </summary>
    public async Task PrepareForPrerollAsync(LavalinkPlayer player, CancellationToken ct = default)
    {
        Cancel();
        if (!IsEnabled) return;
        try
        {
            await player.SetVolumeAsync(FullVolume, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set TTS preroll volume to full.");
        }
    }

    /// <summary>
    /// Plays a music track with volume transitions around it. When fades apply
    /// (enabled + duration known + long enough), the order is mute → play →
    /// spawn ramp monitor, all owned by the controller so the mute can't race
    /// the play. Otherwise just invokes <paramref name="playAction"/>.
    /// </summary>
    public async Task PlayMusicAsync(LavalinkPlayer player, TimeSpan? trackDuration, Func<ValueTask> playAction, CancellationToken ct = default)
    {
        Cancel();

        bool willFade = IsEnabled
            && trackDuration is not null
            && trackDuration.Value.TotalSeconds >= MinTrackSeconds;

        if (willFade)
        {
            try { await player.SetVolumeAsync(Silent, cancellationToken: ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to mute volume before fade-in; track starts loud."); }
        }

        await playAction();

        if (willFade)
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                cts = _currentLoopCts = new CancellationTokenSource();
            }
            _ = Task.Run(() => MonitorAsync(player, trackDuration!.Value, cts.Token));
        }
    }

    private async Task MonitorAsync(LavalinkPlayer player, TimeSpan trackDuration, CancellationToken ct)
    {
        try
        {
            double fadeSec = FadeSeconds;
            float lastSent = -1f;

            while (!ct.IsCancellationRequested)
            {
                if (player.State != PlayerState.Playing && player.State != PlayerState.Paused)
                {
                    return;
                }

                if (player.State == PlayerState.Paused)
                {
                    await Task.Delay(PollInterval, ct);
                    continue;
                }

                var pos = player.Position?.Position ?? TimeSpan.Zero;
                var remaining = trackDuration - pos;
                if (remaining <= TimeSpan.Zero) return;

                // The fade curve is min(head-ramp, tail-ramp) — naturally
                // gives a smooth fade-in, a flat plateau at 1.0, and a fade-out.
                float head = (float)Math.Clamp(pos.TotalSeconds / fadeSec, 0, 1);
                float tail = (float)Math.Clamp(remaining.TotalSeconds / fadeSec, 0, 1);
                float target = Math.Min(head, tail);

                // Skip the REST round-trip during the plateau — Lavalink doesn't
                // need to hear "still 1.0" every 200 ms.
                if (Math.Abs(target - lastSent) > 0.001f)
                {
                    await SafeSetVolumeAsync(player, target);
                    lastSent = target;
                }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch (TaskCanceledException)
        {
            // Normal cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio transition monitor crashed; resetting volume to full.");
            try { await player.SetVolumeAsync(FullVolume); } catch { /* best-effort */ }
        }
    }

    private async Task SafeSetVolumeAsync(LavalinkPlayer player, float volume)
    {
        try
        {
            await player.SetVolumeAsync(volume);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SetVolumeAsync ({Vol:0.00}) failed; continuing.", volume);
        }
    }
}
