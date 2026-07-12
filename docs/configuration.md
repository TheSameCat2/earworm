# Configuration

earworm reads its configuration from three places, in order of precedence (later overrides earlier):

1. **`conf/earworm.yaml`** — checked-in defaults
2. **Environment variables** prefixed `EARWORM_` — useful for container deploys
3. **In-bot settings** stored in SQLite — set with `/config` commands, survives restarts

The YAML and env-var layers configure things you set once at deploy time (API keys, Discord guild ID, file paths, audio defaults). The SQLite layer is for things that change at runtime without redeploying (DJ role, logging channel, DJ on/off).

---

## Secrets — environment variables only

These are **never** in `earworm.yaml`. Set them via shell, `.env`, or your secret store.

| Variable | Required | Description |
|---|:-:|---|
| `EARWORM_DISCORD_BOT_TOKEN` | yes | Bot token from the Discord developer portal |
| `EARWORM_GEMINI_API_KEY` | yes | Google AI Studio API key |
| `EARWORM_ELEVENLABS_API_KEY` | yes | ElevenLabs API key |
| `LAVALINK_PASSWORD` | yes (compose) | Lavalink server password. Consumed by docker-compose.yml; the bot reads it as `EARWORM_Lavalink__Password` |
| `EARWORM_LOG_LEVEL` | no | `Trace` / `Debug` / `Information` / `Warning` / `Error`. Default `Warning` |
| `EARWORM_METRICS_ENABLED` | no | `true` exposes a scaffolded Prometheus endpoint at `/metrics`. Default `false` |
| `TZ` | no | Bot's timezone, e.g. `America/Los_Angeles` |
| `NET_ENVIRONMENT` | no | ASP.NET environment hint. Default `Production` |

---

## `conf/earworm.yaml`

Keys are **PascalCase** because the .NET configuration binder is case-insensitive but doesn't transform `snake_case`. Don't use snake_case — it'll silently fall back to defaults and you'll be debugging "why is my queue cap not applying" for an hour. (Ask me how I know.)

### `Bot`

```yaml
Bot:
  OwnerUserIds:
    - "123456789012345678"
```

| Key | Required | Default | Description |
|---|:-:|---|---|
| `OwnerUserIds` | for multi-tenant | `[]` | Discord user IDs allowed to run `/admin …` (whitelist management). Without at least one, no one can admit new tenant guilds. |

Single-tenant playback remains valid with the default empty list. Before adding
a second guild, configure at least one owner ID and restart Earworm so someone
can run `/admin add-server` and recover from tenant-management mistakes.

### `Discord`

```yaml
Discord:
  GuildId: "798253374361042964"
  NowPlayingChannelId: null
```

| Key | Required | Default | Description |
|---|:-:|---|---|
| `GuildId` | yes | — | **Seed only.** On first run this guild is admitted as the initial tenant and its pre-multi-tenant data is backfilled to it. After that the whitelist lives in the `tenants` table (managed via `/admin`); changing this key does not re-admit or move data. |
| `NowPlayingChannelId` | no | `null` | **Seed only** for the `GuildId` tenant's now-playing channel. Per-guild thereafter — set it for any tenant with `/config now-playing-channel`. If unset, no embed is posted. |

### `Lavalink`

```yaml
Lavalink:
  Host: "localhost"
  Port: 2333
  Password: "youshallnotpass"
```

| Key | Default | Description |
|---|---|---|
| `Host` | `lavalink` | Host the bot connects to. `localhost` for local-dev; `lavalink` (compose service name) in Docker. |
| `Port` | `2333` | Lavalink HTTP/WS port |
| `Password` | `youshallnotpass` | Must match `LAVALINK_SERVER_PASSWORD` on the Lavalink container |

In Docker Compose, `Host` and `Password` are overridden via `EARWORM_Lavalink__Host` / `EARWORM_Lavalink__Password` env vars (already wired in the compose file).

### `Audio`

```yaml
Audio:
  BitrateKbps: 128
  LoudnessLufs: -14.0
  CrossfadeSeconds: 5
  CrossfadeMinTrackSeconds: 15
```

`BitrateKbps` and `LoudnessLufs` are legacy from the pre-Lavalink era — currently unused, governed by Lavalink's `application.yml` if you care.

`CrossfadeSeconds` is the length of the volume ramp applied at music-track boundaries: the last N seconds of each track fade out and the first N of the next fade in. The same machinery brackets DJ TTS prerolls (music fades out → DJ speaks at full → next music fades in), which gives the "ducking" feel without true mixing. Lavalink only plays one stream per voice connection, so there is always a small (~100-500 ms) silent gap at the swap while it loads the next track — this isn't true overlap. Set to `0` to disable both fades.

`CrossfadeMinTrackSeconds` is a safety floor: tracks shorter than this skip the ramp entirely so a 5s fade doesn't dominate an 8s clip. No effect when `CrossfadeSeconds: 0`.

### `Queue`

```yaml
Queue:
  LengthCap: null
  PerTrackLengthCapSeconds: null
  PerRequesterContiguousCap: null
```

| Key | Default | Description |
|---|---|---|
| `LengthCap` | unlimited | Max queue size. New `/play` calls past this return an error. |
| `PerTrackLengthCapSeconds` | unlimited | Reject tracks longer than this (e.g. block 3-hour podcasts). |
| `PerRequesterContiguousCap` | unlimited | Max consecutive queue entries from one user (anti-spam). |

### `Dj`

```yaml
Dj:
  GeminiModel: "gemini-2.5-flash"
  MaxGapTracks: 4
  PersonaPrompt: |
    You are a casual west-coast radio DJ briefly introducing
    the next track. Keep it under 30 words. ...
  Tts:
    VoiceId: "zDBYcuJrpuZ6YQ7AgRUw"
    ModelId: "eleven_turbo_v2_5"
    Stability: 0.5
    SimilarityBoost: 0.75
  TtsScratchDirectory: "./data/tts"
  TtsServeBaseUrl: "http://host.docker.internal:8080"
```

| Key | Default | Description |
|---|---|---|
| `GeminiModel` | `gemini-2.5-flash` | Model ID for commentary generation. **Verify your key has access** — see [troubleshooting.md](troubleshooting.md#gemini-404-models-not-found). |
| `MaxGapTracks` | `4` | Cadence ceiling. The bot rolls 1..N each cycle; smaller N = more frequent commentary. |
| `PersonaPrompt` | west-coast DJ | System prompt for Gemini. `{track_metadata}` is replaced with track title + artist. Tune for vibe. |
| `Tts.VoiceId` | — | ElevenLabs voice ID. Required — get from elevenlabs.io/app/voice-lab. |
| `Tts.ModelId` | `eleven_turbo_v2_5` | ElevenLabs model. Turbo v2.5 is the cheap-and-fast option; switch to `eleven_multilingual_v2` for higher quality. |
| `Tts.Stability` | `0.5` | ElevenLabs voice stability slider |
| `Tts.SimilarityBoost` | `0.75` | ElevenLabs voice similarity slider |
| `TtsScratchDirectory` | `./data/tts` | Where rendered .mp3 files are staged. Deleted after playback. |
| `TtsServeBaseUrl` | `http://host.docker.internal:8080` | URL Lavalink uses to fetch the staged TTS. **Must be reachable from the Lavalink container, not the bot.** In Compose, this is overridden to `http://earworm:8080`. |

To **disable DJ commentary by config**, set `TtsServeBaseUrl: ""`. The runtime `/djoff` slash command is independent and stored in SQLite.

### `Cache`

```yaml
Cache:
  Directory: "./data/cache"
  SizeCapGb: 100
```

Legacy from the pre-Lavalink era. Currently unused — Lavalink handles its own caching internally. Kept so `EarwormConfig` binding doesn't break.

### `Persistence`

```yaml
Persistence:
  SqlitePath: "./data/earworm.db"
  HistoryRetentionCount: 100
  HistoryMaxN: 100
  BackupIntervalHours: 24
  BackupRetentionCount: 7
```

| Key | Default | Description |
|---|---|---|
| `SqlitePath` | `./data/earworm.db` | Path to the SQLite database. In Docker: `/data/earworm.db`. |
| `HistoryRetentionCount` | `100` | Number of historical track plays kept in the DB. Older entries are pruned on each insert. |
| `HistoryMaxN` | `100` | Max `N` value users can pass to `/history`. Clamps to prevent expensive queries. |
| `BackupIntervalHours` | `24` | Reserved for SQLite backup automation (not yet implemented). |
| `BackupRetentionCount` | `7` | Reserved (not yet implemented). |

The reference compose files override the three `./data` paths to absolute
`/data/...` paths that match their volume mount. An existing deployment that
mounts state at `/app/data` should keep its relative YAML paths; it does not
need a migration. Because SQLite runs in WAL mode, never take a live backup by
copying only `earworm.db`; follow the quiesced-directory procedure in
[Deployment](deployment.md#backups).

### `AutoBehavior`

```yaml
AutoBehavior:
  EmptyChannelGraceSeconds: 120
  IdleDisconnectSeconds: 120
```

| Key | Default | Description |
|---|---|---|
| `EmptyChannelGraceSeconds` | `120` | Disconnect from voice after this many seconds of being alone in the channel. |
| `IdleDisconnectSeconds` | `120` | Disconnect after this many seconds with an empty queue. |

Set either to a very large number to effectively disable that behavior.

### `Ops`

```yaml
Ops:
  HttpPort: 8080
  MaxConcurrentDownloads: 2
  WriteQueueCapacity: 1024
  MaxConcurrentTrackResolutions: 8
  MaxConcurrentTrackResolutionsPerGuild: 2
  MaxPendingTrackResolutionsPerGuild: 16
```

| Key | Default | Description |
|---|---|---|
| `HttpPort` | `8080` | Port the in-process HTTP host listens on for `/live`, `/health`, `/metrics`, `/tts/{id}.mp3` |
| `MaxConcurrentDownloads` | `2` | Reserved (no longer used after Lavalink pivot — Lavalink manages its own concurrency). |
| `WriteQueueCapacity` | `1024` | Maximum pending SQLite write jobs held in memory. When full, callers wait for the writer to catch up. Values below `1` are clamped to `1`. |
| `MaxConcurrentTrackResolutions` | `8` | Process-wide limit for concurrent Lavalink track lookups. Additional requests wait; they are not rejected. |
| `MaxConcurrentTrackResolutionsPerGuild` | `2` | Per-guild track-lookup limit. When the global limit is above one, values at or above it are clamped to one below the global limit so one tenant cannot reserve every slot. |
| `MaxPendingTrackResolutionsPerGuild` | `16` | Additional lookups allowed to wait behind a guild's active limit. Requests above this bound fail fast instead of accumulating unbounded work. Negative values are treated as `0`. |

---

## In-bot settings (`/config`)

These live in SQLite, not YAML, so they can be changed by Discord users with the right role without touching the deployment.

| Setting | Set with | Read by | Default |
|---|---|---|---|
| DJ role | `/config dj-role @Role` | All `[DjOnly]`-gated commands | None (Admins only) |
| Logging channel | `/config logging-channel #channel` | TrackFailureHandler | None (failures only logged to stdout) |
| DJ enabled | `/djon` / `/djoff` | DJEngine | Disabled |

View all current settings: `/config show`.

`/config dj-role` requires the **invoker** to hold `MANAGE_ROLES` or `ADMINISTRATOR` (PRD §7 — prevents a DJ from reassigning the role to lock others out). The other two require the DJ role itself.

---

## Environment variable override syntax

The .NET configuration binder accepts nested config via double-underscore separators. So:

```yaml
Dj:
  TtsServeBaseUrl: "http://earworm:8080"
```

…is equivalent to setting `EARWORM_Dj__TtsServeBaseUrl=http://earworm:8080`. This is how the compose file overrides production-specific values without forcing you to maintain a separate YAML for prod.

The `EARWORM_` prefix is stripped by the config builder; the rest of the path is case-insensitive.
