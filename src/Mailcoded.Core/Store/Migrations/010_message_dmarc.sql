-- schema v10
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- 'pass' | 'fail' | 'none' as reported by a trusted receiving server; NULL = unknown, which includes every row that predates this.
-- No index: the predicate filters the date-ordered scan, as has:attachment does.
ALTER TABLE messages ADD COLUMN dmarc TEXT;
