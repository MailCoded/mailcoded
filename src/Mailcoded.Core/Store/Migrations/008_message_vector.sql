-- schema v8
-- PRAGMA user_version is bumped by Migrator from this file's number; never set it here.

CREATE TABLE IF NOT EXISTS vec_model (
    id          INTEGER PRIMARY KEY,
    fingerprint TEXT NOT NULL UNIQUE,
    name        TEXT,
    dim         INTEGER NOT NULL,
    pooling     TEXT NOT NULL,
    created_utc INTEGER NOT NULL
);

-- One row per message, not one per model: comparing vectors from two models is silently meaningless,
-- so a model change replaces rows rather than adding a dimension to them. message_id is the rowid
-- alias, which makes candidate fetch a point lookup. Derived data — dropping this costs only time.
CREATE TABLE IF NOT EXISTS msg_vec (
    message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
    model_id   INTEGER NOT NULL REFERENCES vec_model(id) ON DELETE CASCADE,
    scale      REAL NOT NULL,
    vec        BLOB NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_msg_vec_model ON msg_vec(model_id, message_id);
