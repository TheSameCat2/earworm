using System;

namespace Earworm.Domain.Player;

public sealed record PlayHistoryEntry
{
    public long HistoryId { get; init; }
    public DateTimeOffset PlayedAt { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public int? DurationSeconds { get; init; }
    public int? PlayedSeconds { get; init; }
    public string RequestedByUserId { get; init; } = string.Empty;
    public string RequestedByDisplayName { get; init; } = string.Empty;
    public bool Skipped { get; init; }
    public bool Failed { get; init; }
    public string? FailureReason { get; init; }
    public string GuildId { get; init; } = string.Empty;
}
