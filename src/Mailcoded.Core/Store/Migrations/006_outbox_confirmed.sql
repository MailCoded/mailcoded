-- schema v6
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- send.preview writes an outbox row before a human has agreed to anything, and ListDueOutbox
-- returned every queued row. Nothing called the retry loop in production, so the only thing between
-- an abandoned draft and the wire was the absence of a caller. This column is the difference
-- between "built" and "agreed to": it is set when a confirm token is consumed, and the retry loop
-- now requires it. The 'sending' arm is deliberately unaffected so RELIABILITY 14.4 crash
-- reconciliation still sees its rows.
ALTER TABLE outbox ADD COLUMN confirmed_utc INTEGER;

-- Any existing row that has moved past 'queued', or has ever been attempted, was confirmed under
-- the old rules; without this an in-flight retry would be stranded by the upgrade.
UPDATE outbox SET confirmed_utc = created_utc WHERE state <> 'queued' OR attempts > 0;
