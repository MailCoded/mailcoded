-- schema v17
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- Encrypted mail is a small share of a mailbox, so is:encrypted has few hits and would walk the whole date index
-- looking for them. The index holds only those rows; it serves the positive form, not -is:encrypted.
CREATE INDEX ix_msg_encrypted ON messages(date_utc DESC, id DESC) WHERE encrypted = 1;
