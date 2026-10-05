-- schema v14
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- The server copy of a draft; a NULL state means none was saved. The Folder is the server path
-- because a local Folder row can be re-created while the server copy stays put.
ALTER TABLE outbox ADD COLUMN server_draft_folder TEXT;
ALTER TABLE outbox ADD COLUMN server_draft_uidvalidity INTEGER;
ALTER TABLE outbox ADD COLUMN server_draft_uid INTEGER;
ALTER TABLE outbox ADD COLUMN server_draft_state TEXT;
ALTER TABLE outbox ADD COLUMN server_draft_reason TEXT;
