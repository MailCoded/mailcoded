# 9. Sync and the daemon

[← Contents](README.md)

## `sync`

    mailcoded sync [--account <id>] [--folder <id>]

Connects to the account's IMAP server and pulls changes: new envelopes, changed Flags, removed
messages. With `--folder` only that folder syncs. Sync is idempotent — re-running after an
interruption replays safely — and the server is the source of truth for mail and Flags, so a sync can
only ever bring the local copy closer to what the server holds.

Bodies are not pulled by sync. They are fetched on demand — when you open a message in the TUI, rest
on it with the preview open, or `read` it — and then kept.

## Who runs where

`mailcoded` and `mailcoded-mcp` open the store directly, in their own process, and exit. The terminal
client is different: **`mailcoded-tui` starts a `mailcoded-daemon` and talks to it** over JSON-RPC.
The daemon holds the IMAP connections open, so the cost of TLS and login is paid once a session rather
than once a keypress, and it can sit in IMAP IDLE waiting for new mail.

## One daemon owns a store

A store's live connections belong to exactly one daemon at a time. Whichever starts first takes an OS
lock on `daemon.lock` in the data directory; a second daemon on the same store still serves every
request from the shared database, but opens no watch connections, and tells its client so. In the TUI
that appears once as `Live updates belong to another mailcoded window; press r to refresh here.`; the
daemon itself logs `Another live daemon (pid N) owns this store; watch connections stay closed here.`

That is why two TUI windows on one store both work but only one of them shows new mail arriving. It
is also why the lock is a real OS lock rather than a pid file: when the owner dies, the lock dies with
it, and the next daemon takes over without guessing.

## Live updates

When the TUI subscribes, the daemon opens one IDLE connection per watched folder — the account's
configured watch set, which defaults to INBOX — and turns server signals into notifications: new mail
(`3 new in INBOX`), folder counts, and errors. A bulk operation on the server produces one
notification per folder, not one per message.

An account that cannot authenticate is not watched. If it has no stored credential at all — one
registered with `--no-password`, or the local account `import-eml` creates — the daemon says so once:

    This account has no stored credential, so it cannot sync. Add one with 'mailcoded account reauth',
    or leave it as a local-only store for imported mail.

rather than retrying a login that can never succeed.

## Connections, retries and backoff

Every connection is attempted over IPv4 and IPv6 in parallel and the first to answer wins, so a broken
IPv6 route costs about a quarter of a second rather than a timeout. After a failure the daemon backs
off — one second, doubling, to at most five minutes — with a fixed longer pause after an
authentication refusal, so a dead credential does not hammer the server. `account test` (chapter 8)
tells you what is wrong; `S` in the TUI shows the last error for each account.

## Running the daemon yourself

    mailcoded-daemon [--store <path>] [--log-level off|error|warn|info|debug|trace]
    mailcoded-daemon --one-shot <method> [--params <json>] [--store <path>]

The default mode speaks Content-Length-framed JSON-RPC 2.0 on stdin and stdout. Every log line goes
to stderr, so stdout carries protocol frames only. `--one-shot` serves one request given on the
command line and exits, which is a handy way to poke at it:

    mailcoded-daemon --one-shot account.list

The protocol — every method, notification, framing rule and error code — is in
[docs/rpc.md](../rpc.md). The TUI is its reference client; `MAILCODED_DAEMON=<path>` tells the TUI
which daemon executable to start, otherwise it looks beside itself, then on PATH.
