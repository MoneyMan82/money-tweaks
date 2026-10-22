-- Tweak Truth Index: anonymous shared measurements. The Worker also creates these on first use (src/evidence.ts),
-- so applying this migration is optional. No user id is stored with a result; evidence_quota only rate-limits sharing.
CREATE TABLE IF NOT EXISTS evidence (
  key TEXT PRIMARY KEY, tweak TEXT NOT NULL, kind TEXT NOT NULL, game TEXT, cpu TEXT NOT NULL, gpu TEXT NOT NULL,
  ram_gb INTEGER NOT NULL, os_build INTEGER NOT NULL, app TEXT NOT NULL, metrics TEXT NOT NULL, day TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS evidence_tweak ON evidence (tweak);
CREATE TABLE IF NOT EXISTS evidence_quota (user_id TEXT NOT NULL, day TEXT NOT NULL, n INTEGER NOT NULL, PRIMARY KEY (user_id, day));
