# 14. Reference

[← Contents](README.md)

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
| `MAILCODED_ALLOW_FORGET` | CLI | the same, for `account forget --yes` |
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
| 9 | 1004, 1007 | store corrupt; store full |
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
arrows `home` `end` `backspace` edit · `ctrl-s` preview · `esc` or `ctrl-c` discard, after a `y/n`.

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
| `folders` | folders with counts |
| `stats`, `health` | counters; state |
| `tui` | the terminal client |
| `draft`, `reply <id>` | build a draft |
| `send-preview <draft>` | preview, and mint the token |
| `send-draft <draft> --confirm-token <t>` | send |
| `outbox` | queued, sending, sent, failed |
| `account list`, `test`, `reauth`, `add`, `forget` | accounts |
| `sync` | pull changes |
| `import-eml <dir>` | load `.eml` files |
| `query --sql` | read-only SQL, gated |
| `version`, `help [verb]` | |

Global options: `--json`, `--data-dir <path>`, `--db <path>`, `--quiet`, `--help`.

## Files in the data directory

`store.db` with `-wal` and `-shm` · `blobs/` · `secrets.enc`, and on Linux and macOS `secrets.key` ·
`daemon.lock` · `daemon.owner` — chapter 3.

## Versions

This manual describes mailcoded **0.1.0**, JSON-RPC protocol **1**, CLI output schema **1** — what
`mailcoded version` prints.
