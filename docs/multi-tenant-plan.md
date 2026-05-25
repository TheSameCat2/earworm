# Multi-tenant refactor plan

Status: **planned, not yet implemented**. Designed in feature-dev session 2026-05-25.

## Goal

Move earworm from a single-guild bot to a multi-tenant bot that serves a whitelist of Discord guilds simultaneously. Each whitelisted guild gets its own queue, playback state, snapshot, settings, history, and metrics. Whitelist is administered by bot owners via slash commands; the `tenants` row is shaped for billing + a website that will land later.

## Decisions locked

- **Whitelist source**: DB table `tenants`, seeded from `Bot.OwnerUserIds` config; owner-only `/admin add-server | list-servers | remove-server` commands.
- **Persistence**: single SQLite, add `guild_id` everywhere, widen UNIQUE constraints, recreate composite-PK tables.
- **Migration**: auto-migrate existing rows to the configured `Discord.GuildId`; that YAML field becomes a one-time seed for the whitelist row and is otherwise ignored thereafter.
- **Future-proofing**: `tenants` row carries `plan TEXT DEFAULT 'free'`, `status TEXT DEFAULT 'active'`, `owner_user_id TEXT NULL`, `created_at INTEGER`. No billing logic. Quota check is a no-op hook.
- **Architecture**: generic `PerGuildRegistry<T>` (single ~30-line abstraction); repositories take `string guildId` parameter; `[WhitelistedGuild]` and `[OwnerOnly]` class-level attributes for compile-time enforcement.
- **Slash command registration**: per-whitelisted-guild (instant propagation); `TenantLifecycleListener` re-registers on whitelist changes and on startup for every active tenant.

## Two correctness fixes that must land

1. **`MetricsRepository` ON CONFLICT** — `UserUpsertSqlByColumn` currently uses `ON CONFLICT(user_id)`. Must become `ON CONFLICT(guild_id, user_id)` or upserts silently fail when two guilds share a user.
2. **`PerGuildRegistry<T>` race** — `ConcurrentDictionary.GetOrAdd` can construct two instances and discard one (catastrophic for `PlayerEngine` which subscribes to Lavalink events at ctor). Wrap the dictionary value in `Lazy<T>` with `LazyThreadSafetyMode.ExecutionAndPublication`.

## Single-tenant chokepoints to remove

Verified by exploration agents:

- `PlayerEngine` (src/Earworm/Domain/Player/PlayerEngine.cs): bakes `_guildId` in ctor; silently drops cross-guild TrackEnded events at line 145; `_ttsCompletion` is a single TCS (race bomb); `_currentTrack`/`_isPaused`/`_cachedState` are scalar.
- `QueueManager` (src/Earworm/Domain/Queue/QueueManager.cs): one flat `_queue` List for all guilds.
- `DJEngine` (src/Earworm/Domain/DJ/DJEngine.cs): `_tracksSinceCommentary`/`_targetGap` are scalar (process-wide cadence).
- `AudioTransitionController`: `_currentLoopCts` is scalar (new track in B aborts A's volume fade).
- `NowPlayingPoster`: `_nowPlayingChannelId` scalar from config.
- All repositories ignore `guild_id` on reads. `QueueRepository.ClearQueueAsync` is a nuclear unscoped DELETE.
- Schema: `playback_state CHECK(id=1)`, `snapshot CHECK(id=1)`, `queue UNIQUE(position)`, `settings PRIMARY KEY(key)`, `metrics_per_user PRIMARY KEY(user_id)`.
- Slash commands registered to one guild ID at startup.
- Already-multi-guild-shaped: `VoiceManager` per-guild timer dicts; Lavalink4NET's per-guild player API.

## Migration 003 SQL

```sql
-- 003_multi_tenant.sql

-- tenants table
CREATE TABLE tenants (
    guild_id       TEXT PRIMARY KEY,
    owner_user_id  TEXT,
    plan           TEXT NOT NULL DEFAULT 'free',
    status         TEXT NOT NULL DEFAULT 'active',  -- 'active' | 'suspended' | 'pending'
    created_at     INTEGER NOT NULL
);

-- queue: widen UNIQUE from (position) to (guild_id, position)
ALTER TABLE queue RENAME TO queue_old;
CREATE TABLE queue (
    queue_item_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    position                   INTEGER NOT NULL,
    source_type                TEXT NOT NULL,
    source_id                  TEXT NOT NULL,
    title                      TEXT,
    artist                     TEXT,
    duration_seconds           INTEGER,
    requested_by_user_id       TEXT NOT NULL,
    requested_by_display_name  TEXT NOT NULL,
    queued_at                  INTEGER NOT NULL,
    guild_id                   TEXT NOT NULL,
    UNIQUE(guild_id, position)
);
INSERT INTO queue SELECT * FROM queue_old;
DROP TABLE queue_old;
CREATE INDEX idx_queue_position ON queue(guild_id, position);
CREATE INDEX idx_queue_user     ON queue(requested_by_user_id);

-- playback_state: drop CHECK(id=1) singleton, key by guild_id
CREATE TABLE playback_state_new (
    guild_id                            TEXT PRIMARY KEY,
    is_playing                          INTEGER NOT NULL DEFAULT 0,
    is_paused                           INTEGER NOT NULL DEFAULT 0,
    current_source_type                 TEXT,
    current_source_id                   TEXT,
    current_title                       TEXT,
    current_artist                      TEXT,
    current_duration_seconds            INTEGER,
    current_requested_by_user_id        TEXT,
    current_requested_by_display_name   TEXT,
    current_position_ms                 INTEGER NOT NULL DEFAULT 0,
    voice_channel_id                    TEXT,
    voice_guild_id                      TEXT,
    updated_at                          INTEGER NOT NULL
);
INSERT INTO playback_state_new
  SELECT '', is_playing, is_paused,
         current_source_type, current_source_id, current_title, current_artist,
         current_duration_seconds, current_requested_by_user_id,
         current_requested_by_display_name, current_position_ms,
         voice_channel_id, voice_guild_id, updated_at
  FROM playback_state WHERE id = 1;
DROP TABLE playback_state;
ALTER TABLE playback_state_new RENAME TO playback_state;

-- snapshot: drop singleton, key by guild_id
CREATE TABLE snapshot_new (
    guild_id                             TEXT PRIMARY KEY,
    saved_at                             INTEGER,
    saved_by_user_id                     TEXT,
    current_source_type                  TEXT,
    current_source_id                    TEXT,
    current_title                        TEXT,
    current_artist                       TEXT,
    current_duration_seconds             INTEGER,
    current_requested_by_user_id         TEXT,
    current_requested_by_display_name    TEXT,
    current_position_ms                  INTEGER,
    voice_channel_id                     TEXT,
    voice_guild_id                       TEXT
);
INSERT INTO snapshot_new
  SELECT '', saved_at, saved_by_user_id,
         current_source_type, current_source_id, current_title, current_artist,
         current_duration_seconds, current_requested_by_user_id,
         current_requested_by_display_name, current_position_ms,
         voice_channel_id, voice_guild_id
  FROM snapshot WHERE id = 1;
DROP TABLE snapshot;
ALTER TABLE snapshot_new RENAME TO snapshot;

-- snapshot_queue: drop-recreate so FK retargets snapshot(guild_id)
DROP TABLE snapshot_queue;
CREATE TABLE snapshot_queue (
    snapshot_queue_item_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_guild_id          TEXT NOT NULL REFERENCES snapshot(guild_id) ON DELETE CASCADE,
    position                   INTEGER NOT NULL,
    source_type                TEXT NOT NULL,
    source_id                  TEXT NOT NULL,
    title                      TEXT,
    artist                     TEXT,
    duration_seconds           INTEGER,
    requested_by_user_id       TEXT NOT NULL,
    requested_by_display_name  TEXT NOT NULL,
    queued_at                  INTEGER NOT NULL,
    guild_id                   TEXT NOT NULL,
    UNIQUE(snapshot_guild_id, position)
);
CREATE INDEX idx_snapshot_queue ON snapshot_queue(snapshot_guild_id, position);

-- settings: composite PK (guild_id, key)
CREATE TABLE settings_new (
    guild_id    TEXT NOT NULL,
    key         TEXT NOT NULL,
    value       TEXT NOT NULL,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (guild_id, key)
);
INSERT INTO settings_new SELECT '', key, value, updated_at FROM settings;
DROP TABLE settings;
ALTER TABLE settings_new RENAME TO settings;

-- metrics_global: composite PK (guild_id, metric_key)
CREATE TABLE metrics_global_new (
    guild_id     TEXT NOT NULL,
    metric_key   TEXT NOT NULL,
    metric_value INTEGER NOT NULL DEFAULT 0,
    updated_at   INTEGER NOT NULL,
    PRIMARY KEY (guild_id, metric_key)
);
INSERT INTO metrics_global_new SELECT '', metric_key, metric_value, updated_at FROM metrics_global;
DROP TABLE metrics_global;
ALTER TABLE metrics_global_new RENAME TO metrics_global;

-- metrics_per_user: composite PK (guild_id, user_id)
CREATE TABLE metrics_per_user_new (
    guild_id                TEXT NOT NULL,
    user_id                 TEXT NOT NULL,
    display_name_last_seen  TEXT NOT NULL,
    tracks_queued           INTEGER NOT NULL DEFAULT 0,
    tracks_completed        INTEGER NOT NULL DEFAULT 0,
    listening_seconds       INTEGER NOT NULL DEFAULT 0,
    requests_youtube        INTEGER NOT NULL DEFAULT 0,
    requests_soundcloud     INTEGER NOT NULL DEFAULT 0,
    requests_mp3_upload     INTEGER NOT NULL DEFAULT 0,
    requests_search         INTEGER NOT NULL DEFAULT 0,
    updated_at              INTEGER NOT NULL,
    PRIMARY KEY (guild_id, user_id)
);
INSERT INTO metrics_per_user_new
  SELECT '', user_id, display_name_last_seen,
         tracks_queued, tracks_completed, listening_seconds,
         requests_youtube, requests_soundcloud, requests_mp3_upload,
         requests_search, updated_at
  FROM metrics_per_user;
DROP TABLE metrics_per_user;
ALTER TABLE metrics_per_user_new RENAME TO metrics_per_user;
CREATE INDEX idx_per_user_listening ON metrics_per_user(guild_id, listening_seconds DESC);
CREATE INDEX idx_per_user_queued    ON metrics_per_user(guild_id, tracks_queued DESC);

CREATE INDEX IF NOT EXISTS idx_history_guild_played ON history(guild_id, played_at DESC);
```

A C# post-migrate step in Program.cs (after `SchemaMigrator.Migrate()`) backfills the sentinel `''` rows to `Discord.GuildId` and seeds the tenants row + the now-playing channel setting from YAML. Idempotent via `WHERE guild_id = ''` and `INSERT OR IGNORE`.

## File changes

### New files

- `src/Earworm/Infrastructure/PerGuildRegistry.cs` — generic registry, internal `Lazy<T>`
- `src/Earworm/Domain/Tenants/ITenantService.cs`, `TenantService.cs` — admission + cache
- `src/Earworm/Persistence/Repositories/ITenantRepository.cs`, `TenantRepository.cs`
- `src/Earworm/Discord/Attributes/WhitelistedGuildAttribute.cs`
- `src/Earworm/Discord/Attributes/OwnerOnlyAttribute.cs`
- `src/Earworm/Discord/Commands/AdminCommands.cs` — `/admin add-server | list-servers | remove-server`
- `src/Earworm/Discord/TenantLifecycleListener.cs` — GUILD_CREATE/DELETE, per-guild slash registration
- `src/Earworm/Persistence/Schema/Migrations/003_multi_tenant.sql`

### Modified files

- `src/Earworm/Config/EarwormConfig.cs` — add `BotConfig.OwnerUserIds: List<string>`
- `src/Earworm/Program.cs` — global→per-guild registration removed in favor of `TenantLifecycleListener.RefreshAllAsync()`; replace 5 engine singletons with `PerGuildRegistry<T>` registrations; remove inline `SetPreTrackHook` and `QueueManager.InitializeAsync` (move into factories); add post-migrate backfill step; add `SlashCommandErrored` handler that converts check failures into ephemeral responses; remove the singleton `INSERT OR IGNORE … id = 1` seed step
- `src/Earworm/Domain/Player/PlayerEngine.cs` — take `string guildId` ctor parameter; remove config parse and the cross-guild filter (now always matches)
- `src/Earworm/Domain/Queue/QueueManager.cs` — take `string guildId` ctor parameter; remove flat in-memory list assumption
- `src/Earworm/Domain/DJ/DJEngine.cs` — take `string guildId` ctor parameter
- `src/Earworm/Domain/Player/AudioTransitionController.cs` — take `string guildId` ctor parameter
- `src/Earworm/Discord/NowPlayingPoster.cs` — take `string guildId` ctor parameter; read channel from per-guild settings
- `src/Earworm/Discord/VoiceManager.cs` — inject `PerGuildRegistry<PlayerEngine>` + `PerGuildRegistry<QueueManager>`; replace `_playerEngine` calls with registry lookups
- `src/Earworm/Discord/MessageListener.cs` — call `ITenantService.IsAdmittedAsync` on the message guild before queuing; lookup `QueueManager` from registry
- All five command classes (`PlaybackCommands`, `QueueCommands`, `InfoCommands`, `DJCommands`, `ConfigCommands`) — add `[WhitelistedGuild]` at class level; inject registries instead of engines; lookup per-guild engine via `_playerRegistry.GetOrCreate(ctx.Guild!.Id)`; thread `ctx.Guild.Id.ToString()` to all repository calls
- `src/Earworm/Discord/Attributes/DjOnlyAttribute.cs` — pass `ctx.Guild.Id.ToString()` to `GetDjRoleIdAsync`
- `src/Earworm/Discord/Attributes/RequesterOrDjAttribute.cs` — same
- `src/Earworm/Discord/DiscordGateway.cs` — wire `GuildCreated`/`GuildDeleted`/`GuildAvailable` to `TenantLifecycleListener`
- All 5 repository interfaces + implementations — add `string guildId` parameter; widen WHERE/ON CONFLICT clauses; **fix `MetricsRepository.UserUpsertSqlByColumn` to `ON CONFLICT(guild_id, user_id)`**
- `src/Earworm/Health/HealthEndpoint.cs` — tag `/metrics` per-user counters with `guild_id` label

## Build sequence (eight focused PRs, each shippable on its own)

1. **PR-1: Config + tenants table + TenantService + admin commands** — `BotConfig.OwnerUserIds`, migration 003 (tenants table only, defer other changes), `ITenantService` + `TenantRepository`, `OwnerOnlyAttribute`, `/admin add-server | list | remove`. Existing single-tenant bot keeps working unchanged.
2. **PR-2: Per-guild slash command registration via TenantLifecycleListener** — global slash registration removed; lifecycle listener re-registers on tenant add/remove and at startup. Smoke test: bot still works in the legacy guild.
3. **PR-3: WhitelistedGuard attribute** — applied class-level to all 5 command modules; `SlashCommandErrored` handler returns ephemeral "not authorized". Legacy guild is seeded into `tenants` by the PR-1 backfill so this is a no-op for it.
4. **PR-4: Migration 003 (full schema) + repository signature changes** — composite PKs, widened UNIQUE, `guildId` parameter added to every repository method, `MetricsRepository.UserUpsertSqlByColumn` ON CONFLICT fix, post-migrate C# backfill in Program.cs. All call sites updated to pass `Discord.GuildId` temporarily. Tests pass.
5. **PR-5: `PerGuildRegistry<T>` + per-guild stateful engines** — generic registry with `Lazy<T>`; `PlayerEngine`/`QueueManager`/`DJEngine`/`AudioTransitionController`/`NowPlayingPoster` take `guildId` ctor param; Program.cs DI registrations swap to registries; `VoiceManager` uses registries. Legacy guild still the only tenant — multi-tenant capability now exists but unused.
6. **PR-6: Command-handler refactor** — handlers replace direct engine injection with registry lookups; `ctx.Guild.Id.ToString()` threaded to repository calls.
7. **PR-7: MessageListener + DjOnlyAttribute/RequesterOrDjAttribute multi-tenant** — gate check in MessageListener; per-guild DJ role lookups.
8. **PR-8: Health/metrics per-guild labels** — `/metrics` per-user counters tagged with `guild_id`; `/health` reports per-tenant readiness from registry.

Each PR keeps the bot deployable. After PR-5 the bot is *capable* of multi-tenant; after PR-8 it is fully correct.

## What this plan does NOT include

- Billing logic (Stripe, webhooks, plan enforcement) — schema seam exists; logic lands later.
- Website / public read API — `/api/tenants/{id}` shape will be designed when the website is started.
- Per-tenant snapshot slots (multi-named snapshots) — still one snapshot per guild.
- Lavalink horizontal scaling / per-guild Lavalink routing.
- Quota enforcement (track caps, listener caps per plan) — no-op hook exists.

## Risks to monitor

- **GUILD_DELETE during in-flight playback**: `PerGuildRegistry<T>.Evict` will dispose `PlayerEngine` mid-track. Add a `PlayerEngine.StopAsync()` await before disposal.
- **Slash command rate limits**: re-registering on every tenant add is fine at low cardinality; if onboarding ever batches, debounce in `TenantLifecycleListener`.
- **`metrics_global` is now per-guild** — any current dashboard that reads it loses cross-bot aggregates. Acceptable; document.
- **`Discord.GuildId` YAML field becomes a one-time seed only**. Make it explicit in the config docs + leave a `// seed only` comment in EarwormConfig.cs.
