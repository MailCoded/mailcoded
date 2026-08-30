-- schema v1
-- Connection PRAGMAs (journal_mode, foreign_keys, synchronous, ...) are applied by SqliteStore at
-- open time: journal_mode cannot be changed inside the transaction this file runs in.

CREATE TABLE accounts (
  id INTEGER PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  display_name TEXT,
  provider TEXT NOT NULL CHECK (provider IN ('imap','graph','jmap','gmail')),
  config_json TEXT NOT NULL          -- hosts/ports/auth kind; NEVER secrets
);

CREATE TABLE folders (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  role TEXT,                         -- inbox|sent|drafts|trash|archive|junk|NULL
  uidvalidity INTEGER,
  uidnext INTEGER,
  highestmodseq INTEGER,
  delta_token TEXT,                  -- Graph deltaLink / JMAP state (v0.2+)
  unread_count INTEGER NOT NULL DEFAULT 0,   -- denormalized; see PERFORMANCE.md
  total_count INTEGER NOT NULL DEFAULT 0,
  UNIQUE (account_id, name)
);

CREATE TABLE messages (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
  uid INTEGER,
  message_id TEXT,
  thread_key TEXT,
  date_utc INTEGER,
  from_addr TEXT, to_addrs TEXT, cc_addrs TEXT,
  subject TEXT,
  flags INTEGER NOT NULL DEFAULT 0,  -- bitfield; bit0 = unread
  modseq INTEGER,
  size INTEGER,
  has_attachments INTEGER NOT NULL DEFAULT 0,
  blob_id INTEGER REFERENCES blobs(id),
  body_fetched INTEGER NOT NULL DEFAULT 0,
  UNIQUE (folder_id, uid)
);

CREATE TABLE blobs (
  id INTEGER PRIMARY KEY,
  sha256 TEXT NOT NULL UNIQUE,
  bytes BLOB,                         -- inline if <= 512KB
  ext_path TEXT                       -- content-addressed file if > 512KB
);

CREATE TABLE tags (
  message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
  tag TEXT NOT NULL,
  PRIMARY KEY (message_id, tag)
);

CREATE TABLE body_text (
  message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
  text TEXT NOT NULL                  -- plaintext extraction
);

CREATE TABLE outbox (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id),
  message_id TEXT NOT NULL,           -- pre-assigned at creation, idempotency key
  state TEXT NOT NULL CHECK (state IN ('queued','sending','sent','failed')),
  raw BLOB NOT NULL,
  smtp_response TEXT,
  attempts INTEGER NOT NULL DEFAULT 0,
  next_attempt_utc INTEGER,
  created_utc INTEGER NOT NULL
);

CREATE TABLE sync_log (
  id INTEGER PRIMARY KEY,
  ts INTEGER NOT NULL,
  account_id INTEGER,
  level TEXT, event TEXT, detail TEXT,
  interface TEXT,                     -- cli | mcp | rpc | internal
  agent_host TEXT
);

-- Sync state SPEC §5.3 has no column for: the resumable backfill cursor (edge case 32) and the
-- PERMANENTFLAGS \* observation (edge case 22). Kept out of `folders` so that table stays verbatim.
CREATE TABLE folder_sync_state (
  folder_id INTEGER PRIMARY KEY REFERENCES folders(id) ON DELETE CASCADE,
  backfill_cursor INTEGER,
  accepts_custom_keywords INTEGER NOT NULL DEFAULT 1,
  last_sync_utc INTEGER
);

-- Primary search index: contentless, phrase-capable
CREATE VIRTUAL TABLE msg_fts USING fts5(
  subject, body_text, from_addr, to_addr,
  content='',
  tokenize='porter unicode61 remove_diacritics 2',
  detail='full',          -- keep phrase/NEAR; do NOT use detail=none here
  prefix='2 3'            -- search-as-you-type (validate the size cost in M-perf)
);

-- Secondary index for CJK substring search
CREATE VIRTUAL TABLE msg_fts_cjk USING fts5(
  subject, body_text,
  content='',
  tokenize='trigram',
  detail='none'           -- trigram ignores position data
);

-- Secondary indexes: create AFTER backfill
CREATE INDEX ix_msg_folder_date
  ON messages(folder_id, date_utc DESC, id, subject, from_addr, flags);  -- covering
CREATE INDEX ix_msg_unread   ON messages(folder_id) WHERE (flags & 1) = 0;  -- partial
CREATE INDEX ix_msg_thread   ON messages(thread_key, date_utc DESC, id);
CREATE UNIQUE INDEX ix_msg_folder_uid ON messages(folder_id, uid);

CREATE INDEX ix_msg_message_id ON messages(message_id);
CREATE INDEX ix_outbox_state ON outbox(state, next_attempt_utc);
CREATE INDEX ix_outbox_message_id ON outbox(message_id);
CREATE INDEX ix_sync_log_account ON sync_log(account_id, id DESC);
CREATE INDEX ix_tags_tag ON tags(tag, message_id);
