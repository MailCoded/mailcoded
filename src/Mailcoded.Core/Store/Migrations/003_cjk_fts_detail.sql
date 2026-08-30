-- schema v3
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- SQLite's trigram tokenizer compiles a substring search into a phrase query, and FTS5 rejects
-- every phrase query unless detail='full'. The v1 table therefore failed EVERY CJK search with
-- "fts5: phrase queries are not supported (detail!=full)". Rebuild it with the default detail.
DROP TABLE IF EXISTS msg_fts_cjk;

CREATE VIRTUAL TABLE msg_fts_cjk USING fts5(
  subject, body_text,
  content='',
  tokenize='trigram'
);

INSERT INTO msg_fts_cjk(rowid, subject, body_text)
  SELECT m.id, COALESCE(m.subject, ''), COALESCE(b.text, '')
  FROM messages m
  LEFT JOIN body_text b ON b.message_id = m.id;

INSERT INTO msg_fts_cjk(msg_fts_cjk) VALUES('optimize');
