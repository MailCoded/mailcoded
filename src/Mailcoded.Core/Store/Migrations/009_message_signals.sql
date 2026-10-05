-- schema v9
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- 1 = yes, 0 = no, NULL = unknown: rows that predate this stay NULL until their headers are seen again.
-- No index: both predicates are low selectivity and filter the date-ordered scan, as has:attachment does.
ALTER TABLE messages ADD COLUMN automated INTEGER;
ALTER TABLE messages ADD COLUMN encrypted INTEGER;
