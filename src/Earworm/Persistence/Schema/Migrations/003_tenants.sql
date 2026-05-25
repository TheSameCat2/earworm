-- 003_tenants.sql
CREATE TABLE tenants (
    guild_id       TEXT PRIMARY KEY,
    owner_user_id  TEXT,
    plan           TEXT NOT NULL DEFAULT 'free',
    status         TEXT NOT NULL DEFAULT 'active',
    created_at     INTEGER NOT NULL
);
