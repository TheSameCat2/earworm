using System;

namespace Earworm.Domain.Queue;

public sealed record QueueItem
{
    public long QueueItemId { get; init; }
    public int Position { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public int? DurationSeconds { get; init; }
    public string RequestedByUserId { get; init; } = string.Empty;
    public string RequestedByDisplayName { get; init; } = string.Empty;
    public DateTimeOffset QueuedAt { get; init; }
    public string GuildId { get; init; } = string.Empty;
}
