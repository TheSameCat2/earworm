using System;

namespace Earworm.Domain.Telemetry;

public sealed record UserMetrics
{
    public string UserId { get; init; } = string.Empty;
    public string DisplayNameLastSeen { get; init; } = string.Empty;
    public long TracksQueued { get; init; }
    public long TracksCompleted { get; init; }
    public long ListeningSeconds { get; init; }
    public long RequestsYoutube { get; init; }
    public long RequestsSoundcloud { get; init; }
    public long RequestsMp3Upload { get; init; }
    public long RequestsSearch { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
