-- ============================================================
-- 004_multi_tenant.sql
--
-- Widens every per-guild table from the single-guild schema to a
-- multi-tenant one: guild_id everywhere, composite primary keys, and
-- UNIQUE constraints scoped per guild. The `tenants` table itself was
-- introduced in 003_tenants.sql and is left untouched here.
--
-- `queue`, `history`, and `snapshot_queue` already carried a guild_id
-- column since 001; this migration only widens their UNIQUE/index
-- constraints. `playback_state`, `snapshot`, `settings`,
-- `metrics_global`, and `metrics_per_user` are recreated with a
-- guild_id key, seeding existing rows with the sentinel '' which a
-- post-migrate C# step backfills to the configured Discord.GuildId.
-- ============================================================

-- ------------------------------------------------------------
-- queue: widen UNIQUE(position) → UNIQUE(guild_id, position).
-- guild_id already populated on existing rows.
-- ------------------------------------------------------------
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
INSERT INTO queue (queue_item_id, position, source_type, source_id, title, artist,
                   duration_seconds, requested_by_user_id, requested_by_display_name, queued_at, guild_id)
    SELECT queue_item_id, position, source_type, source_id, title, artist,
           duration_seconds, requested_by_user_id, requested_by_display_name, queued_at, guild_id
    FROM queue_old;
DROP TABLE queue_old;
CREATE INDEX idx_queue_position ON queue(guild_id, position);
CREATE INDEX idx_queue_user     ON queue(requested_by_user_id);

-- ------------------------------------------------------------
-- playback_state: drop CHECK(id=1) singleton, key by guild_id.
-- ------------------------------------------------------------
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

-- ------------------------------------------------------------
-- snapshot: drop singleton, key by guild_id.
-- ------------------------------------------------------------
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

-- ------------------------------------------------------------
-- snapshot_queue: drop-recreate so the FK retargets snapshot(guild_id).
-- The old snapshot_queue.guild_id (already present) becomes the
-- snapshot key after the sentinel backfill below.
-- ------------------------------------------------------------
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
    UNIQUE(snapshot_guild_id, position)
);
CREATE INDEX idx_snapshot_queue ON snapshot_queue(snapshot_guild_id, position);

-- ------------------------------------------------------------
-- settings: composite PK (guild_id, key).
-- ------------------------------------------------------------
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

-- ------------------------------------------------------------
-- metrics_global: composite PK (guild_id, metric_key).
-- ------------------------------------------------------------
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

-- ------------------------------------------------------------
-- metrics_per_user: composite PK (guild_id, user_id).
-- ------------------------------------------------------------
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
