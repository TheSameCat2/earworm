# Architecture

How earworm is put together and why. Written so a new contributor can find their way around the codebase in 15 minutes.

## High-level

earworm is two processes:

1. **The bot** — a .NET 10 process that handles Discord interactions, generates DJ commentary, persists state to SQLite, and tells Lavalink what to play.
2. **Lavalink** — a Java service (third-party, not maintained by us) that owns the Discord voice gateway, fetches audio from YouTube/SoundCloud/HTTP, and streams Opus packets to Discord.

The split exists because Discord's voice gateway is an evolving protocol (v4→v8 over the project's lifetime) and writing a robust in-process Opus pipeline is a maintenance nightmare. Lavalink absorbs that maintenance burden and the bot just talks JSON-over-WebSocket to it.

```
                ┌─────────────────────────────────────────────┐
                │                  Discord                    │
                │      gateway      voice gw      REST        │
                └─────┬──────────────┬─────────────┬──────────┘
                      │              │             │
                      │ ws           │ ws + udp    │ https
                      │              │             │
   ┌──────────────────┴───┐    ┌─────┴─────────┐   │
   │   earworm (.NET)     │    │  Lavalink     │◀──┘
   │   - slash commands   │◀──▶│  - voice gw   │
   │   - mention handler  │ ws │  - opus mux   │
   │   - queue + history  │    │  - yt source  │
   │   - DJ engine        │    └───────────────┘
   │   - ASP.NET endpts   │           ▲
   └──────┬───────┬───────┘           │ http
          │       │                   │
        sqlite  https            (TTS fetch)
          │       │
       earworm.db │
                  ▼
            Gemini API,
            ElevenLabs API
```

## The bot's components

Directory layout under `src/Earworm/`:

```
Program.cs                 # composition root + DI wiring + startup sequence
Config/                    # EarwormConfig record + YAML binder
Discord/
  DiscordGateway.cs        # owns the DiscordClient lifecycle + Ready bit
  MessageListener.cs       # @mention handler (parses, validates, queues)
  VoiceManager.cs          # Lavalink voice connect/disconnect + idle timers
  NowPlayingPoster.cs      # writes the embed on TrackStarted
  Attributes/              # slash command permission checks ([DjOnly], etc.)
  Commands/                # the actual slash command classes
Domain/
  Player/
    PlayerEngine.cs        # the Lavalink-backed playback engine (thin)
    PlaybackState.cs       # snapshot record returned by PlayerEngine.State
    PlayHistoryEntry.cs    # historical track record
    AudioTransitionController.cs  # volume ramps for crossfade + DJ ducking
    TrackFailureHandler.cs # posts failure messages to the logging channel
  Queue/
    QueueManager.cs        # in-memory queue + SQLite persistence + events
    QueueItem.cs           # the queue row type
    TrackQueuingService.cs # resolve→queue helper (used by /play and @mention)
  DJ/
    DJEngine.cs            # cadence + Gemini → ElevenLabs → file stage
    GeminiClient.cs        # HTTP wrapper around Google's API
    ElevenLabsTtsProvider.cs
    ITtsProvider.cs
    TtsPreroll.cs          # the URL+cleanup record returned to PlayerEngine
Persistence/
  StateStore.cs            # SQLite connection lifecycle
  Repositories/            # one per table — Queue, History, Settings, etc.
  Schema/
    SchemaMigrator.cs      # idempotent migration runner
    Migrations/*.sql       # versioned schema changes
Health/
  HealthEndpoint.cs        # ASP.NET minimal API — /health, /metrics, /tts/{id}
```

### The core flow: "play this track"

1. User sends `@earworm <url>` or runs `/play <url>`.
2. **`PlaybackCommands.PlayAsync`** (slash) or **`MessageListener.OnMessageCreatedAsync`** (mention) parses the request.
3. **`TrackQueuingService.ResolveAndQueueAsync`** asks Lavalink to resolve the URL/query into a `LavalinkTrack` (title, artist, duration, source type). The track metadata is added to `QueueManager`.
4. `QueueManager` fires **`TrackQueued`**, which `PlayerEngine.OnTrackQueued` listens to.
5. `PlayerEngine` checks: is a Lavalink player available for this guild? If yes, and nothing is currently playing, dequeue one and play it.
6. `PlayerEngine.PlayNextAsync` invokes the **pre-track hook** (`DJEngine.MaybePlayCommentaryAsync`). If the DJ rolls a hit, it generates text, renders MP3, stages it, and returns a `TtsPreroll`.
7. If a preroll exists, `PlayerEngine` plays it first via Lavalink, awaits the `TrackEnded` from Lavalink, runs the cleanup callback (deletes the staged .mp3), then plays the music track.
8. When the music track ends, Lavalink fires `TrackEnded` globally. `PlayerEngine.OnLavalinkTrackEndedAsync` logs history, increments metrics, and calls `PlayNextAsync` to advance the queue.

### Why the indirect signaling

`QueueManager` and `PlayerEngine` communicate via .NET events, not direct method calls. This means:

- `QueueManager` doesn't know `PlayerEngine` exists. Adding a second listener (e.g., a third-party plugin) is a one-line subscription.
- Tests can substitute a mock `IQueueRepository` and exercise `PlayerEngine` without ever spinning up Lavalink.
- The DJ TTS pre-roll vs music track distinction in the `TrackEnded` handler uses a small piece of mutable state (`_ttsCompletion`) rather than a custom Lavalink player subclass. Less moving parts.

### Crossfade and DJ ducking (audio transitions)

`AudioTransitionController` is a small position-polling state machine that
ramps the player's volume up at the start of every music track and back
down in its last `Audio.CrossfadeSeconds` seconds. The same machinery
brackets DJ TTS prerolls: music fades out → DJ speaks at full → next music
fades in. This gives the "ducking" feel without true mixing.

Constraint: a single `LavalinkPlayer` per guild plays one stream at a
time, so the swap between tracks always has a small (~100-500 ms) silent
gap while Lavalink loads the next URL. The fade hides the abrupt cut but
isn't a true crossfade overlap.

Disable by setting `Audio.CrossfadeSeconds: 0`. Tracks shorter than
`Audio.CrossfadeMinTrackSeconds` skip the ramp.

The controller drives volume by polling `player.Position` every 200 ms
rather than scheduling a `Task.Delay(duration - fade)` timer. The polling
form handles pause / resume / seek for free — paused means position
doesn't advance, so the fade math stays put without explicit handlers.

### Why we don't use `QueuedLavalinkPlayer`

Lavalink4NET ships a `QueuedLavalinkPlayer` class that maintains its own in-memory queue and auto-advances. We deliberately don't use it because:

- Our queue is the source of truth — it's persisted to SQLite, survives restarts, supports snapshots, has per-user metadata, and obeys the PRD's caps.
- Two queues fighting for authority is a recipe for sync bugs ("why did this track disappear when I /removed it from position 3?").

We use the plain `LavalinkPlayer` (no queue) and drive it one track at a time from our `PlayerEngine`. This means Lavalink is a "play this single URL" service for us.

## Startup sequence

`Program.cs` does the following, in order:

1. **Load `.env`** if present (via DotNetEnv). Real shell env wins over the file.
2. **Build config** — `conf/earworm.yaml` → `EarwormConfig` record. Then validate required keys.
3. **Build the ServiceProvider** — register `DiscordClient`, Lavalink services, all bot services.
4. **Resolve the DiscordClient + register slash commands** via `UseSlashCommands` with the shared `ServiceProvider`.
5. **Run SQLite migrations** — idempotent; new schemas get applied, existing DBs are unchanged.
6. **Hydrate QueueManager** from SQLite — any tracks that were pending when the bot last shut down come back.
7. **Wire the DJ pre-track hook** into `PlayerEngine`.
8. **Start the ASP.NET HTTP host** (`/health`, `/metrics`, `/tts/{id}`).
9. **Eager-resolve event-handler singletons** (`MessageListener`, `NowPlayingPoster`, `VoiceManager`, `TrackFailureHandler`) so their ctor-time event subscriptions are wired before Discord events fire.
10. **Connect Discord gateway**.
11. **Start Lavalink connection** — must come after step 10 because Lavalink4NET needs the bot's user ID for its handshake.
12. **Wait on SIGINT** — graceful shutdown reverses the order.

## Why DSharpPlus 4.5.1 (stable), not 5.x nightly

The project briefly tried DSharpPlus 5.0 nightly when 4.5.x's VoiceNext stopped working. After the Lavalink pivot the in-process voice path is gone entirely, so the only reason for 5.x went with it.

4.5.1 stable has:

- A documented API that hasn't changed in months
- The `DSharpPlus.SlashCommands` library (5.x replaced it with `DSharpPlus.Commands`, a different API)
- The classic ctor-based event subscription pattern, which is more readable than 5.x's `DiscordClientBuilder.ConfigureEventHandlers`

We keep the slash-command surface tightly scoped because it's the user-facing API — changes here mean re-registering commands with Discord and propagation delays.

## Persistence

SQLite at `Persistence.SqlitePath` (default `./data/earworm.db` for dev, `/data/earworm.db` in containers). Tables:

| Table | Contents |
|---|---|
| `queue` | Current queue (rebuilt into memory on startup) |
| `playback_state` | Last-known playback position, for resume-from-saved-state (now mostly vestigial since Lavalink doesn't preserve position across reconnects) |
| `history` | Last N played tracks (configurable) |
| `snapshots` | `/save`'d queue snapshots — one per slot, currently one slot |
| `settings` | Key/value: DJ role, logging channel, DJ enabled flag |
| `metrics_global` | Counters: tracks_queued, listening_seconds, requests_youtube, etc. |
| `metrics_user` | Per-user counters: listening_seconds, tracks_queued, tracks_completed |

The `cache_index` table is a leftover from the pre-Lavalink era and isn't used by any code; the migration is kept so deployed DBs don't fail on schema-version checks.

Migrations live in `Persistence/Schema/Migrations/*.sql`, applied in filename-sorted order by `SchemaMigrator`. Each is wrapped in a transaction and recorded in a `schema_versions` table; re-runs are no-ops.

## Multi-tenancy

earworm serves a whitelist of Discord guilds simultaneously. Each admitted guild ("tenant") gets its own queue, playback state, snapshot, settings, history, and metrics — fully isolated.

- **Admission**: the `tenants` table (status `active` | `suspended`) is the whitelist. Bot owners (`Bot.OwnerUserIds`) manage it via `/admin add-server | list-servers | remove-server`. `ITenantService.IsAdmittedAsync` is the gate; the `[WhitelistedGuild]` class attribute enforces it on every user-facing command module, and `MessageListener` checks it before serving an @mention.
- **Per-guild engines**: `PerGuildRegistry<T>` (`Infrastructure/PerGuildRegistry.cs`) lazily constructs and caches one instance per guild of each stateful engine — `QueueManager`, `PlayerEngine`, `DJEngine`, `AudioTransitionController`. It wraps each value in `Lazy<T>` with `ExecutionAndPublication` so a concurrent first-access race can't build two `PlayerEngine`s and leak a Lavalink subscription.
- **The single shared `IAudioService`**: Lavalink4NET's audio service is one process-wide singleton, so every per-guild `PlayerEngine` subscribes to the same global `TrackEnded`. Each engine filters by `e.Player.GuildId != _guildId` — that filter is the event-routing mechanism, not dead defensiveness.
- **Global event bridges**: `VoiceManager`, `NowPlayingPoster`, and `TrackFailureHandler` stay process-wide singletons but subscribe to *every* per-guild engine via `PerGuildRegistry<PlayerEngine>.AddInitializer`, which runs the subscription callback against existing instances and all future ones, exactly once each.
- **Persistence**: every table carries `guild_id`; composite primary keys and `UNIQUE(guild_id, …)` constraints scope rows per guild (migration `004_multi_tenant.sql`). The legacy single-guild `Discord.GuildId` is a one-time seed only — a post-migrate backfill in `Program.cs` rewrites the pre-migration rows to it and seeds its `tenants` row.
- **Slash registration**: `TenantLifecycleListener` registers commands per active tenant guild (instant propagation) at startup and on `/admin add-server`.

## DI conventions

Repositories and the process-wide bridges are **singletons**; the per-guild engines live behind `PerGuildRegistry<T>` singletons (the registry is the singleton; the engines inside are per-guild). Many bridges subscribe to events in their constructors (`MessageListener` → `DiscordClient.MessageCreated`, `NowPlayingPoster` → each `PlayerEngine.TrackStarted`, etc.). The DI container is built once at startup; eager-resolution near the end of `Program.Main` forces those constructors to run before any events arrive, and the active tenants' engines are pre-created so a restored queue resumes after a restart.

`HttpClient` is registered via `services.AddHttpClient<GeminiClient>()` (and similar for `ElevenLabsTtsProvider`). This is the canonical pattern — never construct `new HttpClient()` directly.

`IAudioService` (Lavalink) is registered via `services.AddLavalink()`. It implements `IAsyncDisposable` only (not the sync `IDisposable`), which is why `CompositionRootTests` uses `await using` to dispose its test provider.

## Lavalink configuration

Lavalink v4 ships without YouTube support — Google made the previous lavaplayer YouTube source impossible to keep working. We use the **`youtube-source` plugin** by lavalink-devs, configured in `conf/lavalink/application.yml`.

Plugin versions break whenever YouTube ships new player JS. The symptom is `Must find sig function from script` in Lavalink's logs. The fix is to bump the version in `application.yml` and restart Lavalink — releases come within hours of YouTube's changes.

Client list (`MUSIC`, `ANDROID_VR`, `WEB`, `WEBEMBEDDED`) is the current recommended set. Different clients have different rate limits and different signature requirements; the plugin tries them in order until one works.

## DJ TTS delivery

Probably the most fiddly piece of the system to explain.

1. DJ cadence rolls a hit in `DJEngine.MaybePlayCommentaryAsync`.
2. Gemini generates ~30 words of commentary text.
3. ElevenLabs renders text → MP3 stream.
4. Bot writes MP3 to `Dj.TtsScratchDirectory/<guid>.mp3`.
5. Bot returns `TtsPreroll { Url = "<TtsServeBaseUrl>/tts/<guid>.mp3", OnConsumedAsync = () => File.Delete(...) }` to `PlayerEngine`.
6. `PlayerEngine` asks Lavalink to load the URL via `Tracks.LoadTrackAsync(url, TrackSearchMode.None)`.
7. Lavalink sends an HTTP GET to that URL.
8. `HealthEndpoint`'s `/tts/{file}` route serves the file from disk (with a strict filename regex to prevent path traversal).
9. Lavalink decodes MP3, plays it through the voice connection.
10. Lavalink fires `TrackEnded`. `PlayerEngine` runs the cleanup callback, which deletes the staged file.
11. `PlayerEngine` plays the actual music track.

The cleanup is in a `finally` block, so a failed Lavalink load (file unreachable, decode error, etc.) still triggers deletion — no orphan files.

The `TtsServeBaseUrl` config key handles the routing weirdness:

- **Local dev**: bot runs on host (port 8080), Lavalink in Docker. Lavalink reaches the host via `host.docker.internal` (requires `--add-host host.docker.internal:host-gateway` on the Lavalink container, and a ufw allow rule).
- **Docker compose**: both containers on a shared bridge. Lavalink reaches the bot at `http://earworm:8080`.

## What's intentionally simple

A few "could be better" things that are deliberately not:

- **No connection pooling for SQLite**. We use a single connection guarded by a single-writer channel. The bot's load is tiny.
- **The `cleanup` callback in `TtsPreroll` is fire-and-forget on completion**. A crash between Lavalink finishing and cleanup running leaks a file. `TtsScratchJanitor` sweeps orphans on startup and on a periodic timer (`Dj.TtsScratchMaxAgeMinutes` / `Dj.TtsScratchMaxFiles`), so leaks are bounded regardless of restart frequency.
- **Health endpoint has no auth**. It's only bound to `127.0.0.1` on the host via the compose `ports:` config, so external traffic can't reach it.

## What's deliberately defensive

- **`HealthEndpoint.MapGet("/tts/{file}", ...)`** validates the filename against a 32-hex regex AND verifies the resolved path is under the scratch dir. Two layers of defense against path traversal — overkill, but cheap.
- **`ufw allow in on docker0 to any port 8080 proto tcp`** in the local-dev docs scopes to the docker bridge interface, not the subnet, so a host re-IP doesn't quietly open the port to the LAN.
- **`.dockerignore` excludes `.env`** so a stray dev `.env` can't end up in a built image.

## Adding a new feature

A worked example: "add a `/volume <0-100>` command."

1. Create `Discord/Commands/VolumeCommand.cs` (or add to `PlaybackCommands.cs`).
2. Add the `[SlashCommand("volume", "...")]` method with an `InteractionContext` + `[Option("level", "...")] long level`.
3. Gate it with `[DjOnly]` and probably `[InVoice]`.
4. Resolve `PlayerEngine` from the constructor.
5. Add a `SetVolumeAsync(int level)` method on `PlayerEngine` that gets the Lavalink player and calls `player.SetVolumeAsync(level / 100f)`.
6. Register the command in `Program.cs` (`slash.RegisterCommands<PlaybackCommands>(commandGuildId)` already covers it if you added it to `PlaybackCommands`).
7. Update `docs/commands.md`.
8. Run `dotnet build` and `dotnet test` — the CompositionRootTests will catch DI wiring mistakes.

This pattern works for nearly any new feature. Listen-events go via `PlayerEngine`'s event surface; data needs go via a new repository.
