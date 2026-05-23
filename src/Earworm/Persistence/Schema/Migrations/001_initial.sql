-- ============================================================
-- 001_initial.sql
-- ============================================================

-- Tracks which migrations have been applied. Read at startup
-- by SchemaMigrator.
CREATE TABLE schema_migrations (
    migration_id   INTEGER PRIMARY KEY,
    name           TEXT NOT NULL,
    applied_at     INTEGER NOT NULL          -- unix epoch ms
);

-- ------------------------------------------------------------
-- Cache index: maps source IDs / content hashes to cached
-- audio files on disk.
-- ------------------------------------------------------------
CREATE TABLE cache_index (
    cache_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    source_type       TEXT NOT NULL,         -- 'youtube' | 'soundcloud' | 'mp3_upload'
    source_id         TEXT NOT NULL,         -- canonical video ID, or content hash for uploads
    cache_path        TEXT NOT NULL,         -- absolute path inside the container
    title             TEXT,
    artist            TEXT,
    duration_seconds  INTEGER,
    file_size_bytes   INTEGER NOT NULL,
    first_seen        INTEGER NOT NULL,      -- unix epoch ms
    last_accessed     INTEGER NOT NULL,      -- unix epoch ms; updated on every hit
    loudnorm_measured_i       REAL,
    loudnorm_measured_lra     REAL,
    loudnorm_measured_tp      REAL,
    loudnorm_measured_thresh  REAL,
    loudnorm_offset           REAL,
    UNIQUE(source_type, source_id)
);

CREATE INDEX idx_cache_last_accessed ON cache_index(last_accessed);
CREATE INDEX idx_cache_source        ON cache_index(source_type, source_id);

-- ------------------------------------------------------------
-- Live queue: the upcoming tracks. Currently-playing track
-- is NOT in this table — see playback_state below.
-- ------------------------------------------------------------
CREATE TABLE queue (
    queue_item_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    position                   INTEGER NOT NULL,    -- 0-based; UNIQUE per state
    source_type                TEXT NOT NULL,
    source_id                  TEXT NOT NULL,
    title                      TEXT,
    artist                     TEXT,
    duration_seconds           INTEGER,
    requested_by_user_id       TEXT NOT NULL,       -- Discord snowflake (string-safe)
    requested_by_display_name  TEXT NOT NULL,       -- snapshot at queue time
    queued_at                  INTEGER NOT NULL,    -- unix epoch ms
    guild_id                   TEXT NOT NULL,       -- forward-compat for multi-server
    UNIQUE(position)
);

CREATE INDEX idx_queue_position ON queue(position);
CREATE INDEX idx_queue_user     ON queue(requested_by_user_id);

-- ------------------------------------------------------------
-- Playback state: singleton row (id = 1). Holds what's
-- currently playing and how far into the track.
-- ------------------------------------------------------------
CREATE TABLE playback_state (
    id                          INTEGER PRIMARY KEY CHECK (id = 1),
    is_playing                  INTEGER NOT NULL DEFAULT 0,     -- bool: 0/1
    is_paused                   INTEGER NOT NULL DEFAULT 0,
    current_source_type         TEXT,
    current_source_id           TEXT,
    current_title               TEXT,
    current_artist              TEXT,
    current_duration_seconds    INTEGER,
    current_requested_by_user_id        TEXT,
    current_requested_by_display_name   TEXT,
    current_position_ms         INTEGER NOT NULL DEFAULT 0,
    voice_channel_id            TEXT,                           -- for resume-after-restart
    voice_guild_id              TEXT,
    updated_at                  INTEGER NOT NULL                -- unix epoch ms
);

-- Seed the singleton row.
INSERT INTO playback_state (id, updated_at) VALUES (1, 0);

-- ------------------------------------------------------------
-- Saved snapshot: single slot for v1, normalized into two tables.
-- ------------------------------------------------------------
CREATE TABLE snapshot (
    id                                 INTEGER PRIMARY KEY CHECK (id = 1),
    saved_at                           INTEGER,             -- unix epoch ms; NULL = no snapshot
    saved_by_user_id                   TEXT,
    current_source_type                TEXT,
    current_source_id                  TEXT,
    current_title                      TEXT,
    current_artist                     TEXT,
    current_duration_seconds           INTEGER,
    current_requested_by_user_id       TEXT,
    current_requested_by_display_name  TEXT,
    current_position_ms                INTEGER,
    voice_channel_id                   TEXT,
    voice_guild_id                     TEXT
);

-- Seed the empty snapshot row. saved_at IS NULL means "no snapshot to restore."
INSERT INTO snapshot (id) VALUES (1);

-- Captured queue items at save time, FK'd to the snapshot.
CREATE TABLE snapshot_queue (
    snapshot_queue_item_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id                INTEGER NOT NULL REFERENCES snapshot(id) ON DELETE CASCADE,
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
    UNIQUE(snapshot_id, position)
);

CREATE INDEX idx_snapshot_queue ON snapshot_queue(snapshot_id, position);

-- ------------------------------------------------------------
-- Settings: typed key-value config that's mutable at runtime.
-- ------------------------------------------------------------
CREATE TABLE settings (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,                  -- always stringified; typed in code
    updated_at  INTEGER NOT NULL
);

-- ------------------------------------------------------------
-- Play history: row-by-row log of every play attempt.
-- ------------------------------------------------------------
CREATE TABLE history (
    history_id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    played_at                  INTEGER NOT NULL,        -- track start time, unix epoch ms
    source_type                TEXT NOT NULL,
    source_id                  TEXT NOT NULL,
    title                      TEXT,
    artist                     TEXT,
    duration_seconds           INTEGER,
    played_seconds             INTEGER,                 -- how much actually played
    requested_by_user_id       TEXT NOT NULL,
    requested_by_display_name  TEXT NOT NULL,
    skipped                    INTEGER NOT NULL DEFAULT 0,   -- bool
    failed                     INTEGER NOT NULL DEFAULT 0,   -- bool
    failure_reason             TEXT,
    guild_id                   TEXT NOT NULL
);

CREATE INDEX idx_history_played_at ON history(played_at DESC);
CREATE INDEX idx_history_user      ON history(requested_by_user_id);

-- ------------------------------------------------------------
-- Global metrics: system-wide counters.
-- ------------------------------------------------------------
CREATE TABLE metrics_global (
    metric_key   TEXT PRIMARY KEY,
    metric_value INTEGER NOT NULL DEFAULT 0,
    updated_at   INTEGER NOT NULL
);

-- ------------------------------------------------------------
-- Per-user metrics.
-- ------------------------------------------------------------
CREATE TABLE metrics_per_user (
    user_id                     TEXT PRIMARY KEY,
    display_name_last_seen      TEXT NOT NULL,           -- updated on every interaction
    tracks_queued               INTEGER NOT NULL DEFAULT 0,
    tracks_completed            INTEGER NOT NULL DEFAULT 0,    -- played fully
    listening_seconds           INTEGER NOT NULL DEFAULT 0,
    requests_youtube            INTEGER NOT NULL DEFAULT 0,
    requests_soundcloud         INTEGER NOT NULL DEFAULT 0,
    requests_mp3_upload         INTEGER NOT NULL DEFAULT 0,
    requests_search             INTEGER NOT NULL DEFAULT 0,
    updated_at                  INTEGER NOT NULL
);

CREATE INDEX idx_per_user_listening ON metrics_per_user(listening_seconds DESC);
CREATE INDEX idx_per_user_queued    ON metrics_per_user(tracks_queued DESC);
