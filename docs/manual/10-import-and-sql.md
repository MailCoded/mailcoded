# 10. Importing mail and raw SQL

[← Contents](README.md)

## `import-eml`

    mailcoded import-eml <dir> [--folder INBOX] [--account <id>] [--recursive] [--email <addr>]

Parses every `.eml` file in a directory, stores the raw message, indexes subject, sender and body for
search, and threads it. Import is local: nothing is uploaded anywhere.

    $ mailcoded import-eml ~/old-mail --recursive
    imported 1204 of 1204 file(s) into folder 1; 0 skipped

Files are imported in filename order and keyed by their position, so re-running the same directory
updates the same rows instead of duplicating them. Empty files and files over 64 MiB are skipped and
listed. Imported messages start unread.

### The local account

If the store has no account yet, import creates one to hold the files — `local@import.mailcoded.test`,
or the `--email` you give — pointing at `localhost:993` with no credential. It is a container, not a
mailbox: it cannot sync, and the daemon says so once rather than trying (chapter 9). Once you add a
real account, pass `--account` to say which one an import belongs to.

The repository ships 37 anonymised fixtures in `fixtures/eml/`, which is a good way to try everything
in this manual without a mail server:

    mailcoded --db /tmp/mail.db import-eml fixtures/eml
    mailcoded --db /tmp/mail.db tui

## `query --sql`

    MAILCODED_ENABLE_SQL=1 mailcoded query --sql '<select>' [--max-rows 200] [--read-only]

Read-only SQL against the store. It is **off** unless `MAILCODED_ENABLE_SQL=1` is set:

    mailcoded: forbidden (1006): Raw SQL reads require MAILCODED_ENABLE_SQL=1.
      hint: Set MAILCODED_ENABLE_SQL=1, or use the search/read/thread verbs instead.

With it, the connection runs under `PRAGMA query_only`, a single `SELECT` or `WITH` statement is
accepted, and the result is capped at `--max-rows` (1 to 1000, default 200). You never receive a
handle to the database file. `--read-only` affirms the only mode there is.

    $ MAILCODED_ENABLE_SQL=1 mailcoded query --sql 'SELECT folder_id, COUNT(*) AS n FROM messages GROUP BY folder_id'
    folder_id	n
    1	37
    [1 row(s), truncated=false]

The tables are `accounts`, `folders`, `messages`, `blobs`, `tags`, `body_text`, `outbox` and
`sync_log`. **Column names are internal and change with migrations**; `search`, `read` and `thread`
are the stable contract. Use SQL for the aggregates the verbs do not expose.

### Reading the audit trail

`sync_log` is append-only and records, among other things, every send attempt — allowed or denied —
every tag change, every body fetch, and every read made from the agent surface. The body of a message
is never written to it.

    MAILCODED_ENABLE_SQL=1 mailcoded query --max-rows 50 --json \
      --sql "SELECT ts, level, interface, agent_host, event, detail FROM sync_log ORDER BY id DESC"

Or, from your own shell, straight at the file:

    sqlite3 -readonly ~/.local/share/mailcoded/store.db \
      "SELECT ts, interface, event, detail FROM sync_log ORDER BY id DESC LIMIT 50;"
