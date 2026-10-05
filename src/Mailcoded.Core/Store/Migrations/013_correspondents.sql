-- schema v13
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- People the user has written to, learned from outgoing mail only. The key is the whole address, lower-cased.
CREATE TABLE correspondents (
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  address TEXT NOT NULL,
  display_name TEXT,
  use_count INTEGER NOT NULL,
  last_used_utc INTEGER NOT NULL,
  PRIMARY KEY (account_id, address)
) WITHOUT ROWID;

-- One row once the Sent mail that predates this table has been counted; the store runs that pass when it opens.
CREATE TABLE correspondent_backfill (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  completed_utc INTEGER NOT NULL
);
