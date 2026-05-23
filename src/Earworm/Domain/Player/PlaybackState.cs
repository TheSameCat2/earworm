using System;

namespace Earworm.Domain.Player;

public sealed record PlaybackState
{
    public bool IsPlaying { get; init; }
    public bool IsPaused { get; init; }
    public string? CurrentSourceType { get; init; }
    public string? CurrentSourceId { get; init; }
    public string? CurrentTitle { get; init; }
    public string? CurrentArtist { get; init; }
    public int? CurrentDurationSeconds { get; init; }
    public string? CurrentRequestedByUserId { get; init; }
    public string? CurrentRequestedByDisplayName { get; init; }
    public int CurrentPositionMs { get; init; }
    public string? VoiceChannelId { get; init; }
    public string? VoiceGuildId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
