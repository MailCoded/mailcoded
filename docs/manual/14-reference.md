# 14. Reference

[← Contents](./)

## Environment variables

| Variable | Read by | Effect |
|---|---|---|
| `MAILCODED_DATA_DIR` | all | the data directory (chapter 3); `--data-dir` and `--db` override it |
| `MAILCODED_DB` | `mailcoded-mcp` | the path to `store.db` when `--db` is not passed |
| `MAILCODED_DAEMON` | `mailcoded-tui` | which `mailcoded-daemon` executable to start; `mailcoded tui` sets it to the sibling it found |
| `MAILCODED_SEND` | CLI, MCP | `1`, `true` or `yes` opens the agent-surface send gate |
| `MAILCODED_APPROVED_RECIPIENTS` | CLI, MCP | addresses and `@domain` or `*@domain` patterns every recipient must match; empty approves nobody |
| `MAILCODED_ENABLE_SQL` | CLI | `1` enables `query --sql` |
| `MAILCODED_ALLOW_MOVE` | CLI | `1`, `true` or `yes`; required for `move` and `archive` on the CLI at all. A terminal still confirms; `--yes` skips the prompt |
| `MAILCODED_ALLOW_BACKUP` | CLI | `1`, `true` or `yes`; required for `backup` and `backup-verify` on the CLI at all. Never honoured over MCP, which has no such tool |
| `MAILCODED_ALLOW_FORGET` | CLI | the same, for `account forget --yes` |
| `MAILCODED_ALLOW_TRUST_CHANGE` | CLI | the same, for `account trust --yes` and for `account add --trust-authserv-id ... --yes`: changing which servers' `Authentication-Results` are believed |
| `MAILCODED_ALLOW_IDENTITY_CHANGE` | CLI | the same, for `account identity add --yes`: widening which addresses mail can be sent as |
| `MAILCODED_ALLOW_SERVER_DRAFTS` | CLI | the same, for `account server-drafts on --yes`: also saving each draft to the server's Drafts folder |
| `MAILCODED_SECRET_BACKEND` | all | `auto` (default), `file`, `libsecret`, `keychain`, `wincred` |
| `MAILCODED_SECRET_KEY` | all | a passphrase for the encrypted-file vault, instead of the machine key |
| `MAILCODED_SECRET_KEY_FILE` | all | a file holding that passphrase |
| `MAILCODED_OAUTH_CLIENT_ID` | all | your own Microsoft public-client application id |
| `MAILCODED_OAUTH_TENANT` | all | `common` (default), `consumers`, `organizations`, or a tenant GUID |
| `MAILCODED_AGENT_HOST` | CLI, MCP | a label recorded on every audit row |
| `MAILCODED_MCP_LOG` | `mailcoded-mcp` | its stderr log level |

`MAILCODED_TRANSCRIPT` and `MAILCODED_PARENT_PID` are used by the test suite and between the hosts;
they are not for users.

## Exit codes and RPC error codes

| Exit | RPC | |
|---|---|---|
| 0 | | success |
| 1 | | internal error |
| 2 | -32602 | validation; invalid params |
| 3 | 1002 | not found |
| 4 | 1006 | forbidden — a gate refused |
| 5 | 1005 | rate limited |
| 6 | 1003 | confirm required |
| 7 | 1000 | auth |
| 8 | 1001 | network |
| 9 | 1004, 1007 | store corrupt; store full; `backup-verify` found a difference |
| 10 | 1008 | unsupported |
| 130 | | cancelled |

`mailcoded-tui` exits 0, or 2 (the console cannot render ANSI), 3 (the daemon went away, or `--check`
failed), 4 (the daemon refused the connection).

## Every key in the TUI

**Everywhere** — `?` help · `ctrl-l` redraw · `ctrl-c` quit · `esc` cancel the innermost thing.

**Sidebar and message list**

| | |
|---|---|
| `j` `k` `↓` `↑` | move |
| `g` `G` `Home` `End` | first, last |
| `space` `PgDn` `PgUp` | a page |
| `ctrl-d` `ctrl-u` | half a page |
| `n` | the next 100 messages |
| `tab` `h` `l` `←` `→` | switch pane; in the sidebar `h` and `l` fold |
| `enter` | open the folder; read the message |
| `/` | search; `esc` clears it |
| `r` | sync this folder |
| `c` | compose |
| `p` | preview pane on or off |
| `u` `f` `t` `a` `m` `T` | unread, flagged, tags, archive, move, thread |
| `A` `S` `o` | test the account, status, outbox |
| `q` | quit |

**Reader** — the scroll keys above · `r` `R` reply, reply to all · `u` `f` `t` `a` `m` `T` as above ·
`s` then a digit to save an attachment · `q` or `esc` back.

**Move picker** — `j` `k` choose · `enter` move here · `esc` cancel.

**Composer** — `tab` `shift-tab` field · `enter` a new line in the body, otherwise the next field ·
arrows `home` `end` `backspace` edit · `ctrl-n` complete the recipient · `ctrl-s` preview · `esc` or `ctrl-c` discard, after a `y/n`.

**Confirmation** — `Y` sends · anything else goes back.

**Help, status, outbox** — any key closes.

**Mouse, on Linux and macOS** — click to select · double-click to open · wheel to scroll · click a
key-bar entry · shift for the terminal's own selection.

## The verbs

| | |
|---|---|
| `setup` | add an account, interactively |
| `search '<query>'` | search the local store |
| `read <id>` | one message, as plaintext |
| `thread <id\|key>` | one conversation |
| `attachments <id> [--save <n>]` | list them, or save one |
| `tag <id> +a -b` | Tags and Flags |
| `move <id> --folder <f>` | move on the server, confirmed |
| `archive <id>` | move to Archive, confirmed |
| `folders` | the folder tree with counts |
| `correspondents [prefix]` | people you have written to, best first |
| `stats`, `health` | counters; state |
| `tui` | the terminal client |
| `draft`, `reply <id>` | build a draft |
| `unsubscribe <id>` | draft the sender's mailto unsubscribe; never opens a link |
| `send-preview <draft>` | preview, and mint the token |
| `send-draft <draft> --confirm-token <t>` | send |
| `outbox` | queued, sending, sent, failed |
| `account list`, `test`, `reauth`, `trust`, `identity`, `signature`, `server-drafts`, `add`, `forget` | accounts |
| `sync` | pull changes |
| `import-eml <dir>` | load `.eml` files |
| `backup <dir>`, `backup-verify <dir>` | a verifiable copy of the store; check one (gated, chapter 3) |
| `query --sql` | read-only SQL, gated |
| `version`, `help [verb]` | |

Global options: `--json`, `--data-dir <path>`, `--db <path>`, `--quiet`, `--help`.

## Options by verb

Every verb also takes the global options. An option a verb does not list here is a usage error (exit 2),
never ignored.

| Verb | Options |
|---|---|
| `search` | `--limit` `--cursor` `--account` `--folder` `--order` `--no-snippet` `--meaning` |
| `read` | `--max-chars` `--skip-chars` `--plaintext` `--no-fetch` |
| `thread` | `--limit` |
| `attachments` | `--save` `--out` `--no-fetch` `--overwrite` |
| `tag` | `--local` |
| `move` | `--folder` `--yes` |
| `archive` | `--yes` |
| `folders` | `--account` |
| `correspondents` | `--account` `--limit` |
| `stats` | none |
| `health` | none |
| `draft` | `--to` `--cc` `--bcc` `--subject` `--body-file` `--body-stdin` `--from` `--signature` `--no-signature` `--reply-to` `--in-reply-to` `--account` |
| `reply` | `--body` `--body-file` `--all` `--no-quote` `--no-fetch` `--signature` `--no-signature` |
| `unsubscribe` | `--no-fetch` |
| `send-preview` | none |
| `send-draft` | `--confirm-token` `--no-append` |
| `outbox` | `--state` |
| `setup` | `--email` `--display-name` `--imap-host` `--imap-port` `--smtp-host` `--smtp-port` `--client-id` `--tenant` `--trust-authserv-id` `--yes` |
| `account add` | `--email` `--display-name` `--imap-host` `--imap-port` `--imap-security` `--imap-user` `--smtp-host` `--smtp-port` `--smtp-security` `--smtp-user` `--secret-ref` `--trust-authserv-id` `--password-stdin` `--no-password` `--yes` |
| `account list` | none |
| `account test` | `--account` `--no-smtp` |
| `account reauth` | `--account` `--client-id` `--tenant` |
| `account trust` | `--account` `--authserv-id` `--none` `--yes` |
| `account identity` | `--account` `--name` `--reply-to` `--yes` |
| `account signature` | `--account` `--file` `--stdin` `--none` |
| `account server-drafts` | `--account` `--yes` |
| `account forget` | `--account` `--yes` |
| `sync` | `--account` `--folder` |
| `import-eml` | `--account` `--folder` `--email` `--recursive` |
| `backup` | none |
| `backup-verify` | none |
| `query` | `--sql` `--max-rows` `--read-only` |

Notes. `draft --reply-to` is the local id of the message being replied to, while `account identity
--reply-to` is an address for a Reply-To header. `account identity` takes `list`, `add <address>` or
`forget <address>`; `account signature` takes `list`, `set <name>`, `forget <name>` or
`default new|reply <name>`; `account server-drafts` takes `on` or `off`. `backup` and `backup-verify`
take the destination folder as their argument and nothing else. Chapter 3 covers restoring.

## Search operators

| Operator | Matches |
|---|---|
| bare words, `"a phrase"` | full text over subject, sender, recipients and body; never attachment text |
| `from:` `to:` `cc:` `subject:` | the field contains the value |
| `tag:` | has the local Tag (`tag:auto.news`, `tag:triaged`) |
| `folder:` `in:` | in that Folder, by path, last segment or role (`in:trash`); `in:anywhere` means every Folder |
| `is:unread` `is:read` `is:seen` `is:flagged` `is:starred` `is:unflagged` `is:draft` `is:replied` `is:answered` | the Flag or Tag state |
| `is:automated` | list and bulk mail, auto-replies, delivery and read reports, robot senders |
| `is:encrypted` | PGP/MIME, S/MIME enveloped data or inline PGP armor; the body is not readable |
| `is:verified` | a server you trust reported DMARC pass for the From domain |
| `has:attachment` | the message carries an attachment |
| `attachment:` | text inside a text-like attachment or a `.docx`; also `attachment:"two words"` and `attachment:inv*` |
| `before:` `after:` `since:` | a date, `YYYY-MM-DD`; `before` is strictly earlier, `after` (alias `since`) is the day itself or later |
| `-` in front | negation. For `is:automated` and `is:encrypted` an unknown message matches neither the positive nor the negated form; `-is:verified` also matches unknown and never-evaluated mail |

Trash and Junk Folders are left out unless the query names a Folder, says `in:anywhere`, or the call is
scoped with `--folder`. A negated Folder (`-in:inbox`) does not count as naming one.

Category Tags the engine may give a message once, when it first stores it, from fixed rules:
`auto.social`, `auto.news`, `auto.marketing`, `auto.notifications`. They are local-only: the `auto.`
prefix is never sent to a server as a keyword.

## Error names

`error.name` in the CLI's JSON error object, with the exit code and RPC code each carries.

| `error.name` | Exit | RPC | Raised for |
|---|---|---|---|
| `invalid_params` | 2 | -32602 | a bad option, argument or value |
| `sender_not_allowed` | 2 | -32602 | a draft `From` the account does not own (over RPC this is `data.reason`) |
| `not_found` | 3 | 1002 | no such message, folder, account, draft or file |
| `forbidden` | 4 | 1006 | a gate is closed: send, raw SQL, backup, move |
| `rate_limited` | 5 | 1005 | the send budget, or a busy store |
| `confirm_required` | 6 | 1003 | `send-draft` without a valid token |
| `auth` | 7 | 1000 | the credential failed or is missing |
| `network` | 8 | 1001 | a connection failed; retry later |
| `store_corrupt` | 9 | 1004 | the store failed a check |
| `store_full` | 9 | 1007 | the disk is full |
| `unsupported` | 10 | 1008 | the server or build lacks a capability |
| `permanent` | 10 | 1008 | a server refused for good |
| `io_error`, `internal_error` | 1 | -32603 | a local I/O failure, or a bug |
| `cancelled` | 130 | | interrupted |

`backup-verify` is not an error object: when it finds differences it prints its full report with
`"ok": false` on standard output and exits 9.

## Files in the data directory

`store.db` with `-wal` and `-shm` · `blobs/` · `secrets.enc`, and on Linux and macOS `secrets.key` ·
`daemon.lock` · `daemon.owner` — chapter 3.

## Versions

This manual describes mailcoded **0.1.0**, JSON-RPC protocol **1**, CLI output schema **1** — what
`mailcoded version` prints.
