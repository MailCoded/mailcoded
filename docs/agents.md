# Giving an agent access to mailcoded

This document is for the human who decides what an AI agent may do with their mail. It
covers installing the CLI for an agent host, the default capability posture, how to turn
the two off-by-default capabilities on, what is recorded and where to read it, and the
risk you accept by doing any of this.

The normative source is `docs/AGENT-INTERFACE.md` (SPEC §13). Where this document and
that one disagree, that one wins.

## The shape of the thing

mailcoded keeps mail in a local SQLite database plus a blob directory. Three hosts sit on
top of the same `Mailcoded.Core` library:

| Binary | Surface | Who talks to it |
|---|---|---|
| `mailcoded` | one-shot CLI, JSON on stdout | shell-capable agents (Claude Code, Codex CLI, Gemini CLI, opencode, Goose) |
| `mailcoded-mcp` | MCP server over stdio | agent hosts that cannot run a shell (Claude Desktop, Cursor) |
| `mailcoded-daemon` | JSON-RPC over stdio | the interactive editor client, not agents |

Every safety gate lives in `Mailcoded.Core`. The CLI and the MCP server are thin skins
with identical enforcement; you cannot loosen one by choosing the other.

The store lives outside any workspace folder:

- Linux: `$XDG_DATA_HOME/mailcoded` (default `~/.local/share/mailcoded`)
- macOS: `~/Library/Application Support/mailcoded`
- Windows: `%LOCALAPPDATA%\mailcoded`

with `store.db` and `blobs/` inside it. `MAILCODED_DATA_DIR`, `--data-dir <path>`, and
`--db <path>` override it, in increasing order of specificity.

## Installing the CLI for an agent host

```bash
dotnet publish src/Mailcoded.Cli -c Release -r linux-x64 -p:PublishAot=true
install -m 0755 src/Mailcoded.Cli/bin/Release/net10.0/linux-x64/publish/mailcoded ~/.local/bin/
mailcoded help
```

Use the runtime identifier for your machine (`osx-arm64`, `win-x64`, …). The CLI is AOT and needs
no .NET runtime, but it is not one file: copy `libe_sqlite3.so` beside it or it will not start.

Then set up an account and confirm the store answers:

```bash
printf '%s' "$IMAP_PASSWORD" | mailcoded account add \
  --email me@example.com --imap-host imap.example.com \
  --smtp-host smtp.example.com --password-stdin --json
mailcoded sync --json
mailcoded health --json
```

The password is read from stdin only. It is never accepted as an argument, never written
to the database or config, and never echoed; it goes to the OS keyring, or to an
encrypted file vault when no keyring is present (`health` reports which, as
`secret_backend`).

For a host that supports Agent Skills, install the repo's `SKILL.md` — it is the agent's
copy of the safety rules, the exit-code table, and the pagination contract. Point the
host at it however that host installs skills; the file itself is self-contained.

Optionally set `MAILCODED_AGENT_HOST=<label>` in the agent's environment. It is recorded
on every audit row, which is what lets you tell later which host did what. When it is
unset, mailcoded makes a best-effort guess from `CLAUDECODE`, `CLAUDE_CODE_ENTRYPOINT`,
or `TERM_PROGRAM`.

## Default posture

This is the matrix from AGENT-INTERFACE §13.6, as implemented in
`Mailcoded.Core.Application.AgentPolicy`. It is a function of the caller kind alone: the
CLI and MCP are "agent surfaces"; the editor's RPC client is not.

| Capability | Default for an agent | How it unlocks |
|---|---|---|
| search / read / thread | ON | — |
| tag / triage | ON | — |
| draft | ON | — |
| send | **OFF** | `MAILCODED_SEND=1` + `MAILCODED_APPROVED_RECIPIENTS` + a one-time token + ≤5/hour |
| raw SQL read | **OFF** | `MAILCODED_ENABLE_SQL=1`; read-only and row-capped even then |
| HTML bodies | **never on this surface** | not available to an agent at all |
| move between folders | **never on this surface** | not available to an agent at all |
| delete / expunge / trash | **does not exist** | never; there is no such verb or tool |

Two consequences worth understanding. Bodies reach the agent as plaintext only — that is
what removes markdown links and remote images, the EchoLeak-class exfiltration channel,
from the agent's context. And no-delete is enforced by absence: the agent has no write
handle to the database, so an injected `DELETE FROM messages` has nothing to run against.

## Turning send on, deliberately

Sending is two locks plus a key, and all three are required.

**Lock 1 — the master switch.** `MAILCODED_SEND=1` (also accepts `true`/`yes`) in the
agent's environment.

**Lock 2 — the recipient allowlist.** `MAILCODED_APPROVED_RECIPIENTS` is a comma-,
semicolon-, or whitespace-separated list. Entries are exact addresses (`bob@example.com`)
or domains (`@example.com`, `*@example.com`). **An empty or unset list approves nobody**,
so setting `MAILCODED_SEND=1` alone changes nothing. Every recipient — To, Cc, and Bcc —
must match, or the send is denied.

**The key — a one-time token from a human.** Sending is two-phase:

```bash
mailcoded draft --to bob@example.com --subject 'Re: invoice' --body-file /tmp/reply.txt --json
# -> draft_id, plus a "send" object reporting the current gate posture
mailcoded send-preview <draft_id> --json
# -> the exact From/To/Cc/Bcc, size, and a confirm_token valid for 10 minutes
mailcoded send-draft <draft_id> --confirm-token <token> --json
```

`draft` never prints a token. `send-preview` shows you exactly who would receive the
message and mints the token; you are meant to read that preview and decide. `send-draft`
without a valid, unexpired, unconsumed token fails with exit 6 (RPC 1003); with a closed
gate it fails with exit 4 (RPC 1006); over budget it fails with exit 5 and a
`retry_after_ms`. There is a ceiling of 5 agent sends per rolling hour regardless.

**Where the token lives, and what that means.** The token is persisted in the store as a
salted hash, not held in process memory: `send-preview` reports `confirm_token_scope:
"store"`. So the pair above is a **real send from the bare CLI** — the token minted by one
one-shot `send-preview` process is redeemable by the later `send-draft` process. It is
single-use, bound to that one draft and its Message-ID, expires 10 minutes after it is
minted, and is compared in constant time; the token value itself is never written to the
store, the log, or an audit row. Treat the preview output as a bearer capability: within
those 10 minutes, whoever can read the agent's stdout can spend it.

The 5-per-rolling-hour budget is store-backed too, so one window is shared by every
process on the machine — CLI, MCP server and daemon draw down the same allowance, and
starting a fresh process does not reset it.

Everything above is enforced inside Core. Running the agent through MCP instead of the
CLI does not change it, and neither does running one command per shell invocation.

## Turning raw SQL on

```bash
MAILCODED_ENABLE_SQL=1 mailcoded query --read-only --max-rows 50 --json \
  --sql 'SELECT folder_id, COUNT(*) FROM messages GROUP BY folder_id'
```

Without the environment variable the verb fails with exit 4. With it, the connection runs
under `PRAGMA query_only`, only a single `SELECT` or `WITH` statement is accepted, and
results are capped (`--max-rows`, 1..1000, default 200). The agent never receives a file
handle to the database, the blobs, or a Maildir.

Leave it off unless you have a reason. It exposes internal column names that change with
migrations, so an agent that learns them writes queries that break later — and it hands
the agent the whole corpus in one call rather than one search at a time.

## What is audited, and where to read it

Audit rows are appended to the `sync_log` table in `store.db`. The table is append-only:
nothing in the codebase rewrites or deletes a row. Columns are `id`, `ts`, `account_id`,
`level` (`info`/`warn`/`error`), `event`, `detail`, `interface` (`cli` | `mcp` | `rpc` |
`internal`), and `agent_host`.

Recorded from the agent surface: `cli_read`, `cli_thread`, `cli_folders`, `cli_stats`,
`cli_health`, `cli_import`, `tags_set`, `body_fetched`, `sql_query`, and the send trail —
`send_preview`, `send_attempt`, `send_result`, `sent_append`. Send rows carry the
decision (`allowed` / `denied` / `rate-limited`), the recipient count, whether a token was
consumed, and a digest of the message; **the body itself is never written to the log**.
Denied attempts are logged exactly like allowed ones, at `warn`.

Two honest caveats. `search` itself is not audited — the queries an agent runs are not
recorded, only the messages it opens. And the log is a table in the same database the
mail lives in, so it is only as trustworthy as that file's filesystem permissions.

To read it:

```bash
MAILCODED_ENABLE_SQL=1 mailcoded query --read-only --max-rows 200 --json \
  --sql "SELECT ts, level, interface, agent_host, event, detail FROM sync_log ORDER BY id DESC"
```

or, from your own shell rather than the agent's, directly:

```bash
sqlite3 -readonly ~/.local/share/mailcoded/store.db \
  "SELECT ts, interface, agent_host, event, detail FROM sync_log ORDER BY id DESC LIMIT 50;"
```

`mailcoded health --json` gives the live posture in one line: `send_enabled`,
`approved_recipient_patterns`, `raw_sql_enabled`, plus the store path and schema version.
`mailcoded stats --json` reports `remaining_sends_in_window` and outstanding confirm
tokens; both are read from the store, so every process reports the same numbers.

## The residual risk you are accepting

Read this before enabling anything.

The "lethal trifecta" is the combination of untrusted input, access to private data, and
a channel to the outside world. An agent with all three can be talked into leaking your
mail by an email you did not write.

**mailcoded does not eliminate the private-data leg — it widens it.** Giving an agent
search and read gives it your entire mailbox, one query at a time; enabling raw SQL gives
it the corpus in bulk. Mediating those reads through a CLI instead of a file handle keeps
the schema boundary and the audit trail, but it does not reduce what the agent can see.
That is a deliberate trade, not an oversight: an email assistant that cannot read email
is not a product.

It is an acceptable trade only because the other two legs are constrained:

- **Untrusted input is unavoidable but declawed.** Bodies reach the agent as plaintext
  only. No HTML, no markdown rendering, no remote image fetch — the usual zero-click
  exfiltration channels (an attacker-supplied image URL that carries your data in its
  query string) are not present on this surface. Attachment bytes are never returned.
- **External communication is gated four ways.** Send is off by default, requires an
  explicit recipient allowlist, requires a fresh one-time token that a human saw a
  preview before handing over, is capped at 5 per hour, and writes an audit row whether
  it is allowed or denied. State change beyond that is limited to tags; there is no
  delete and no move.

That is Meta's "Agents Rule of Two" applied: no more than two of {untrusted input,
sensitive data, state change or external comms} without human supervision — and the
confirm token *is* the supervision. Set `MAILCODED_SEND=1` with a broad allowlist like
`@example.com` and you have moved that dial yourself; the token is then the only thing
left between an injected instruction and a sent message.

The residual risk that no gate addresses: an agent can still be induced to *summarize
your mail back to you inaccurately*, or to act on a forged instruction in a way that is
merely wrong rather than exfiltrating. Read what it tells you with the same suspicion you
would apply to the email it read.

## MCP setup (Claude Desktop, Cursor)

Publish the MCP host and point the client at it. Note this is the one host that does not
publish AOT, so it ships as a framework-dependent binary.

```bash
dotnet publish src/Mailcoded.Mcp -c Release
```

Claude Desktop, in `claude_desktop_config.json`
(macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`;
Windows: `%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "mailcoded": {
      "command": "/absolute/path/to/mailcoded-mcp",
      "args": ["--db", "/absolute/path/to/store.db"],
      "env": {
        "MAILCODED_AGENT_HOST": "claude-desktop"
      }
    }
  }
}
```

`--db` may be omitted, in which case `MAILCODED_DB` or the default store path is used.
Absolute paths are required: the client launches the process with an unpredictable
working directory. Restart the client after editing the file.

The server advertises eight tools — `search`, `read`, `thread`, `tag`, `draft`,
`send_preview`, `send_draft`, `stats`. There is no removal tool, by construction rather
than by configuration. Bodies come back as tool results in plaintext. There are no
new-mail notifications; the agent polls `search` and `stats`. Transport is stdio, and
stdout carries protocol frames only — all logging goes to stderr.

To unlock sending here, put `MAILCODED_SEND` and `MAILCODED_APPROVED_RECIPIENTS` in that
`env` block. The `send_preview` → `send_draft` token flow works end to end: `send_preview`
returns `confirm_token` in its result and `send_draft` requires it as an argument, with the
gate enforced in Core rather than in the adapter. The token and the hourly budget live in
the store, so this surface and the CLI share one window rather than one each.

**Reach caveat (AGENT-INTERFACE §13.5).** This is a *local stdio* server. It reaches
Claude Desktop's local config and Cursor's no-terminal mode. It does **not** reach
claude.ai on the web or the mobile apps: those connectors are remote-brokered and would
need a deliberately-secured remote HTTP deployment, which is explicitly out of scope. If
you want mail access from a phone, this is not the mechanism.

## Turning it back off

Unset `MAILCODED_SEND`, `MAILCODED_APPROVED_RECIPIENTS`, and `MAILCODED_ENABLE_SQL` in
the agent host's environment (for MCP, remove the `env` entries and restart the client).
Everything returns to the default posture immediately — the gates are read from the
environment at process start, and no "unlocked" state is persisted in the store. An
outstanding confirm token and the spent-send window *are* rows in the store and survive a
restart, but a leftover token buys nothing with the gate shut: the gate is evaluated
before the token is even looked at. To remove the surface entirely, delete the binary from
`PATH` and remove the MCP server entry; the mail store is untouched by either.
