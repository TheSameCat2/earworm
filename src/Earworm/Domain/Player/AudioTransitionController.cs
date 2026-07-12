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
/// the controller leaves player volume untouched unless it must undo a fade it
/// previously owned. Thus disabling crossfade still means "don't touch volume"
/// (forward-compatible with a future /volume), while a short track after a
/// faded track cannot inherit silence.
/// </summary>
public sealed class AudioTransitionController
{
    private const float FullVolume = 1.0f;
    private const float Silent = 0.0f;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinVolumeInterval = TimeSpan.FromMilliseconds(500);

    private readonly EarwormConfig _config;
    private readonly ILogger<AudioTransitionController> _logger;

    private readonly object _lock = new();
    // Lavalink volume updates are independent REST requests. A monitor can be
    // canceled while one request is already in flight; without serialization,
    // that stale response may arrive after the next track's mute/full-volume
    // handoff and overwrite it. New-track writes wait for the prior request and
    // are therefore always the final authoritative update.
    private readonly SemaphoreSlim _volumeWriteGate = new(1, 1);
    private CancellationTokenSource? _currentLoopCts;
    // True after this controller has changed the player's volume for a fade.
    // The flag survives cancellation of the monitor because cancellation does
    // not restore the volume that Lavalink last received.
    private bool _ownsVolume;

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
            await SetVolumeOrderedAsync(
                (volume, cancellationToken) => player.SetVolumeAsync(volume, cancellationToken: cancellationToken),
                FullVolume,
                ct);
            lock (_lock) _ownsVolume = false;
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
    /// the play. A non-faded track normally leaves volume untouched, except when
    /// the prior track was faded by this controller; in that case it restores
    /// full volume before playback so the prior tail fade cannot mute the track.
    /// </summary>
    public async Task PlayMusicAsync(LavalinkPlayer player, TimeSpan? trackDuration, Func<ValueTask> playAction, CancellationToken ct = default)
    {
        Cancel();

        bool willFade = IsEnabled
            && trackDuration is not null
            && trackDuration.Value.TotalSeconds >= MinTrackSeconds;

        await PrepareMusicVolumeAsync(
            willFade,
            (volume, cancellationToken) => player.SetVolumeAsync(volume, cancellationToken: cancellationToken),
            ct);

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

    /// <summary>
    /// Applies the pre-play volume handoff. Kept separate from player playback
    /// so the ownership state machine can be regression-tested without a live
    /// Lavalink connection.
    /// </summary>
    private async Task PrepareMusicVolumeAsync(
        bool willFade,
        Func<float, CancellationToken, ValueTask> setVolumeAsync,
        CancellationToken ct)
    {
        if (willFade)
        {
            // Treat the volume as controller-owned even if this first request
            // fails: the monitor will continue attempting ramp updates, and a
            // later non-faded track should still make a best-effort reset.
            lock (_lock) _ownsVolume = true;
            try { await SetVolumeOrderedAsync(setVolumeAsync, Silent, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to mute volume before fade-in; track starts loud."); }
            return;
        }

        bool restoreFull;
        lock (_lock) restoreFull = _ownsVolume;
        if (!restoreFull) return;

        try
        {
            await SetVolumeOrderedAsync(setVolumeAsync, FullVolume, ct);
            lock (_lock) _ownsVolume = false;
        }
        catch (Exception ex)
        {
            // Retain ownership so a later track can retry the reset.
            _logger.LogWarning(ex, "Failed to restore volume after the prior fade; track may start quiet.");
        }
    }

    private async Task MonitorAsync(LavalinkPlayer player, TimeSpan trackDuration, CancellationToken ct)
    {
        try
        {
            double fadeSec = FadeSeconds;
            float lastSent = -1f;
            DateTime lastVolumeCall = DateTime.MinValue;

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

                // Rate-limit: skip the REST round-trip if we just sent a volume
                // update within MinVolumeInterval. Lavalink's volume endpoint is
                // a REST call — pounding it every 200ms across N guilds generates
                // unnecessary load.
                var now = DateTime.UtcNow;
                bool volumeChanged = Math.Abs(target - lastSent) > 0.001f;
                bool rateLimitPassed = (now - lastVolumeCall) >= MinVolumeInterval;

                if (volumeChanged && rateLimitPassed)
                {
                    await SafeSetVolumeAsync(player, target, ct);
                    lastSent = target;
                    lastVolumeCall = now;
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
            if (ct.IsCancellationRequested) return;
            _logger.LogWarning(ex, "Audio transition monitor crashed; resetting volume to full.");
            try
            {
                await SetVolumeOrderedAsync(
                    (volume, cancellationToken) => player.SetVolumeAsync(volume, cancellationToken: cancellationToken),
                    FullVolume,
                    ct);
                lock (_lock) _ownsVolume = false;
            }
            catch { /* best-effort */ }
        }
    }

    private async Task SafeSetVolumeAsync(LavalinkPlayer player, float volume, CancellationToken ct)
    {
        try
        {
            await SetVolumeOrderedAsync(
                (target, cancellationToken) => player.SetVolumeAsync(target, cancellationToken: cancellationToken),
                volume,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The monitor was superseded before it could publish this update.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SetVolumeAsync ({Vol:0.00}) failed; continuing.", volume);
        }
    }

    private async Task SetVolumeOrderedAsync(
        Func<float, CancellationToken, ValueTask> setVolumeAsync,
        float volume,
        CancellationToken ct)
    {
        await _volumeWriteGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Once issued, let this small REST request finish rather than
            // canceling its client-side wait. The next generation is queued on
            // _volumeWriteGate and will publish its value afterward, so an old
            // server response cannot become the final volume state.
            await setVolumeAsync(volume, CancellationToken.None);
        }
        finally
        {
            _volumeWriteGate.Release();
        }
    }
}
