-- schema v2
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- The OutboxMessage aggregate already models all of this; v1 could not persist any of it.
ALTER TABLE outbox ADD COLUMN permanently_failed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE outbox ADD COLUMN enhanced_status TEXT;
ALTER TABLE outbox ADD COLUMN last_attempt_utc INTEGER;
ALTER TABLE outbox ADD COLUMN max_attempts INTEGER;

-- From/To/Cc/Bcc captured at preview time. An inbound parse can never recover Bcc from the raw
-- bytes, so without this a daemon restart silently drops Bcc recipients on a retry.
ALTER TABLE outbox ADD COLUMN envelope_json TEXT;

-- The due query now also picks up retryable `failed` rows.
CREATE INDEX ix_outbox_due ON outbox(state, permanently_failed, next_attempt_utc);

-- CONDSTORE reports flag changes but never expunges, so the full-diff cadence needs an anchor.
ALTER TABLE folder_sync_state ADD COLUMN last_full_diff_utc INTEGER;

-- Same corrections as the v1 file, for a database created before them: bit0 is Unread, the page
-- order is (date_utc DESC, id DESC), and UNIQUE (folder_id, uid) already indexes that pair.
DROP INDEX IF EXISTS ix_msg_folder_uid;
DROP INDEX IF EXISTS ix_msg_unread;
CREATE INDEX ix_msg_unread ON messages(folder_id) WHERE (flags & 1) = 1;
DROP INDEX IF EXISTS ix_msg_folder_date;
CREATE INDEX ix_msg_folder_date ON messages(folder_id, date_utc DESC, id DESC, subject, from_addr, flags);
