-- schema v11
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- The sanitized List-Unsubscribe header, a line feed, then List-Unsubscribe-Post when present; '' = headers seen, none offered.
-- NULL = unknown, which includes every row that predates this. Parsed on read; no index.
ALTER TABLE messages ADD COLUMN unsubscribe TEXT;
