-- schema v7
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- ix_msg_folder_date is folder-first, so it can order within one folder and not across a mailbox.
-- Every cross-folder recency query therefore fell back to a scan and sort: measured at 500k rows,
-- ORDER BY date_utc DESC, id DESC LIMIT 2000 costs 189.5 ms without this index and 0.5 ms with it,
-- which is the whole search budget spent before ranking begins. Costs ~7.2 MB at 500k messages.
CREATE INDEX IF NOT EXISTS ix_msg_date ON messages(date_utc DESC, id DESC);
