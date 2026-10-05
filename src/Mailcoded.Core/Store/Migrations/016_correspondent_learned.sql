-- schema v16
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- One row per outgoing message already counted. Keyed on the Message-ID, not on the messages row, because a
-- UIDVALIDITY change deletes and re-ingests that row and the same mail must not be counted twice.
-- A message without a Message-ID is keyed on a hash of its headers.
CREATE TABLE correspondent_learned (
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  msg_key TEXT NOT NULL,
  PRIMARY KEY (account_id, msg_key)
) WITHOUT ROWID;

-- A store whose backfill already ran has counted these; one whose backfill is still pending counts them itself.
INSERT OR IGNORE INTO correspondent_learned (account_id, msg_key)
SELECT m.account_id, 'm:' || trim(m.message_id)
FROM messages m JOIN folders f ON f.id = m.folder_id
WHERE f.role = 'sent' AND m.message_id IS NOT NULL AND trim(m.message_id) <> ''
  AND EXISTS (SELECT 1 FROM correspondent_backfill);
