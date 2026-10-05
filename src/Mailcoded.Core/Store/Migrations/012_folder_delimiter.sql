-- schema v12
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- The server's hierarchy delimiter for this folder: one character, '' for a flat namespace (NIL).
-- NULL = not listed since this column exists; read as '/' until the next LIST.
ALTER TABLE folders ADD COLUMN delimiter TEXT;
