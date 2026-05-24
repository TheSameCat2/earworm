-- 002_drop_cache_index.sql
-- cache_index was defined in 001_initial.sql for a planned audio-caching
-- feature that was never wired up. Drop it to keep the schema honest.

DROP INDEX IF EXISTS idx_cache_last_accessed;
DROP INDEX IF EXISTS idx_cache_source;
DROP TABLE IF EXISTS cache_index;
