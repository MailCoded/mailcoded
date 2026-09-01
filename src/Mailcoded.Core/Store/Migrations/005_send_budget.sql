-- schema v5
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

-- The CLI is one-shot, so an hourly send budget held in process memory is no budget at all. A row
-- records only that an agent send happened: no addresses, no subject, no body, no digest. Rows are
-- pruned once they fall out of the window, so the table stays bounded by the budget itself.
CREATE TABLE send_budget (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  sent_utc   INTEGER NOT NULL,
  interface  TEXT NOT NULL,
  recipients INTEGER NOT NULL
);

CREATE INDEX ix_send_budget_sent ON send_budget(sent_utc);
