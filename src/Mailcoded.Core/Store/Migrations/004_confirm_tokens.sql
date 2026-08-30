-- schema v4
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- The CLI is one-shot, so a confirm token held only in process memory can never be redeemed.
-- Only a salted hash is stored, over a preimage that also binds the outbox row, its Message-ID
-- and the digest of the built bytes, so a token cannot be replayed against a different draft.
CREATE TABLE confirm_tokens (
  outbox_id   INTEGER PRIMARY KEY REFERENCES outbox(id) ON DELETE CASCADE,
  salt        BLOB NOT NULL,
  token_hash  BLOB NOT NULL,
  issued_utc  INTEGER NOT NULL,
  expires_utc INTEGER NOT NULL
);

CREATE INDEX ix_confirm_tokens_expiry ON confirm_tokens(expires_utc);
