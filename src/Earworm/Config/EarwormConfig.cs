namespace Earworm.Config;

public sealed record EarwormConfig
{
    public DiscordConfig Discord { get; init; } = new();
    public AudioConfig Audio { get; init; } = new();
    public QueueConfig Queue { get; init; } = new();
    public DjConfig Dj { get; init; } = new();
    public CacheConfig Cache { get; init; } = new();
    public PersistenceConfig Persistence { get; init; } = new();
    public AutoBehaviorConfig AutoBehavior { get; init; } = new();
    public OpsConfig Ops { get; init; } = new();
    public LavalinkConfig Lavalink { get; init; } = new();
}

public sealed record LavalinkConfig
{
    public string Host { get; init; } = "lavalink";
    public int Port { get; init; } = 2333;
    public string Password { get; init; } = "youshallnotpass";
}

public sealed record DiscordConfig
{
    public string GuildId { get; init; } = string.Empty;
    public string? NowPlayingChannelId { get; init; }
}

public sealed record AudioConfig
{
    // Legacy keys from the pre-Lavalink era. Currently unused — audio quality
    // is governed by Lavalink's application.yml.
    public int BitrateKbps { get; init; } = 128;
    public double LoudnessLufs { get; init; } = -14.0;

    // Volume-ramp length applied at music-track boundaries (fade-in at start,
    // fade-out at end) and around DJ TTS prerolls. 0 disables both.
    public int CrossfadeSeconds { get; init; } = 5;

    // Tracks shorter than this skip the ramp so a 5s fade doesn't dominate an
    // 8s clip. No effect when CrossfadeSeconds is 0.
    public int CrossfadeMinTrackSeconds { get; init; } = 15;
}

public sealed record QueueConfig
{
    public int? LengthCap { get; init; }
    public int? PerTrackLengthCapSeconds { get; init; }
    public int? PerRequesterContiguousCap { get; init; }
}

public sealed record DjConfig
{
    public string GeminiModel { get; init; } = "gemini-2.5-flash";
    public int MaxGapTracks { get; init; } = 4;
    public string PersonaPrompt { get; init; } =
        "You are a casual west-coast radio DJ briefly introducing the next track. Keep it under 30 words. " +
        "Be warm and upbeat but not over-the-top. Don't invent facts about the song or artist — only state what's in the metadata. " +
        "Next track: {track_metadata}.";
    public TtsConfig Tts { get; init; } = new();

    /// <summary>
    /// Local directory where rendered TTS .mp3 files are staged before
    /// Lavalink fetches them via the HTTP endpoint. Files are deleted once
    /// playback completes.
    /// </summary>
    public string TtsScratchDirectory { get; init; } = "./data/tts";

    /// <summary>
    /// Base URL Lavalink uses to fetch staged TTS files. Must be reachable
    /// from the Lavalink container/host, NOT from the bot's perspective.
    ///   - Local dev (Lavalink in Docker, bot on host): "http://host.docker.internal:8080".
    ///     Requires running the Lavalink container with --add-host host.docker.internal:host-gateway.
    ///   - Docker compose: "http://earworm:8080" (service name, set in docker-compose.yml).
    /// Empty string disables DJ commentary entirely.
    /// </summary>
    public string TtsServeBaseUrl { get; init; } = "http://host.docker.internal:8080";

    /// <summary>
    /// Maximum age (in minutes) for files in <see cref="TtsScratchDirectory"/> before
    /// the periodic retention sweep deletes them. Default 60 minutes.
    /// </summary>
    public int TtsScratchMaxAgeMinutes { get; init; } = 60;

    /// <summary>
    /// Maximum number of files allowed in <see cref="TtsScratchDirectory"/> at any
    /// one time. If the count exceeds this, the oldest excess files are deleted.
    /// Default 100.
    /// </summary>
    public int TtsScratchMaxFiles { get; init; } = 100;
}

public sealed record TtsConfig
{
    public string VoiceId { get; init; } = string.Empty;
    public string ModelId { get; init; } = "eleven_turbo_v2_5";
    public double Stability { get; init; } = 0.5;
    public double SimilarityBoost { get; init; } = 0.75;
}

public sealed record CacheConfig
{
    public string Directory { get; init; } = "/data/cache";
    public int SizeCapGb { get; init; } = 100;
}

public sealed record PersistenceConfig
{
    public string SqlitePath { get; init; } = "/data/earworm.db";
    public int HistoryRetentionCount { get; init; } = 100;
    /// <summary>
    /// Max N a user may pass to /history. PRD §7: "max value configurable in conf".
    /// </summary>
    public int HistoryMaxN { get; init; } = 100;
    public int BackupIntervalHours { get; init; } = 24;
    public int BackupRetentionCount { get; init; } = 7;
}

public sealed record AutoBehaviorConfig
{
    public int EmptyChannelGraceSeconds { get; init; } = 120;
    public int IdleDisconnectSeconds { get; init; } = 120;
}

public sealed record OpsConfig
{
    public int HttpPort { get; init; } = 8080;
    public int MaxConcurrentDownloads { get; init; } = 2;
}
