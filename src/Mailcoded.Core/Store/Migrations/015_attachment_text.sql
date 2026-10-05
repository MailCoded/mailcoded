-- schema v15
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- Derived data, rebuildable from the stored raw message; idx is the ParsedAttachment.Index the MIME walk
-- assigns, so a hit names an attachment the way attachment.get does.
CREATE TABLE attachment_text (
  id INTEGER PRIMARY KEY,
  message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
  idx INTEGER NOT NULL,
  file_name TEXT,
  mime_type TEXT NOT NULL,
  text TEXT NOT NULL,
  truncated INTEGER NOT NULL DEFAULT 0,
  UNIQUE (message_id, idx)
);

-- One row per message read for attachment text, even when nothing qualified; the backfill queue is the
-- absence of a row at the current extractor version.
CREATE TABLE attachment_index (
  message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
  version INTEGER NOT NULL
);

-- Only messages the backfill can read: the queue scan never visits the rest of the table.
CREATE INDEX ix_msg_attachment_queue
  ON messages(id) WHERE body_fetched = 1 AND has_attachments = 1 AND blob_id IS NOT NULL;

-- contentless_delete lets a row go by rowid alone, so removal never hands back the indexed text.
-- detail stays 'full' so quoted phrases keep working.
CREATE VIRTUAL TABLE att_fts USING fts5(
  text,
  content='',
  contentless_delete=1,
  tokenize='porter unicode61 remove_diacritics 2'
);

-- Rows exist only for text that holds CJK characters, the only text a CJK query can match.
CREATE VIRTUAL TABLE att_fts_cjk USING fts5(
  text,
  content='',
  contentless_delete=1,
  tokenize='trigram'
);
