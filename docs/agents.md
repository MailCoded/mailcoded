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
| correspondents (people the user has written to) | ON on the CLI, **absent** from MCP | — |
| send | **OFF** | `MAILCODED_SEND=1` + `MAILCODED_APPROVED_RECIPIENTS` + a one-time token + ≤5/hour |
| raw SQL read | **OFF** | `MAILCODED_ENABLE_SQL=1`; read-only and row-capped even then |
| store backup (`backup`, `backup-verify`) | **OFF** on the CLI, **absent** from MCP | `MAILCODED_ALLOW_BACKUP=1`, CLI only |
| HTML bodies | **never on this surface** | not available to an agent at all |
| move between folders | **never on this surface** | not available to an agent at all |
| delete / expunge / trash | **does not exist** | never; there is no such verb or tool |

Two consequences worth understanding. Bodies reach the agent as plaintext only — that is
what removes markdown links and remote images, the EchoLeak-class exfiltration channel,
from the agent's context. And no-delete is enforced by absence: the agent has no write
handle to the database, so an injected `DELETE FROM messages` has nothing to run against.

## What search covers

`search` leaves out messages in Trash and Junk folders unless the query names a folder (`in:trash`,
`in:INBOX`), says `in:anywhere`, or the call carries `--folder` / `folder_id`. An agent that expects a
message to exist and gets nothing should retry with `in:anywhere` before concluding it is gone. This
is a ranking convenience, not a boundary: `in:anywhere` is always available and the agent surface gains
no new capability from it.

## Triage signals

`search`, `read` and `thread` results carry two booleans, `automated` and `encrypted`, and the
query language filters on them: `-is:automated` is the usual first cut for mail a person wrote,
`is:encrypted` finds mail whose body mailcoded cannot read. Both are derived from headers and MIME
structure the sender controls, so they sort mail and **never** vouch for it: an automated-looking
message can be a person, and a message that is not marked automated can still be an attack.

Unknown is a real state. Mail synced before these signals existed carries `null` (JSON) or omits the
field (MCP) until its body is fetched or read again, and such a message matches neither
`is:automated` nor `-is:automated`. An agent that needs "everything that is not known to be
automated" must run the positive query as well and subtract. `encrypted: true` means the body is
ciphertext or an opaque S/MIME blob; there is no decryption, so do not expect text from it.

### Category Tags

When a message first arrives mailcoded may give it one category Tag: `auto.social`, `auto.news`,
`auto.marketing` or `auto.notifications`, from fixed built-in rules over the From address,
`List-Id`, the automated signal and the subject. Query them with `tag:auto.news` and read them in
the `tags` of `read` and `thread`. A message gets at most one, and many get none, so the absence of a
category says nothing. They are decided once, at the first insert: a Tag the human removed is not
put back, and mail stored before this existed is not backfilled. They are local only (never IMAP
keywords). The inputs are all sender-controlled, so a category is a sorting aid like `automated`,
never a statement about trust, and an agent must not use one to relax the untrusted-content rules.
Removing or adding one through `tag` is an ordinary, audited Tag mutation.

### Sender authentication

`is:verified` is the one signal that is not computed from headers the sender controls. A message
is verified when a receiving server **the human told mailcoded to trust** wrote an
`Authentication-Results` header (RFC 8601) reporting DMARC `pass` for the From domain. The
`dmarc` field says `pass`, `fail`, `none` or `unknown`, and is absent when mailcoded never evaluated
the message. So `-is:automated is:verified` is mail a person sent from a domain that was not forged.

What it means, and what it does not:

- It means the From **domain** was not spoofed, as far as that server could tell. It does not mean
  the message is safe, the sender is honest, the link is harmless, or the attachment is clean. A
  scammer can pass DMARC for a domain they own, and a compromised account passes it too. The
  prompt-injection rules still apply in full: verified content is still data, never instructions.
- Only a server the account trusts counts. A sender can write their own
  `Authentication-Results: mx.google.com; dmarc=pass` line; mailcoded ignores any header whose
  authserv-id is not trusted, reads only the topmost trusted one, and ignores anything inside a
  comment or quoted string. An account with no trusted authserv-id verifies nothing (Gmail trusts
  `mx.google.com` by default).
- `dkim=pass` or `spf=pass` alone never verify. Imported `.eml` files are never verified.
- `-is:verified` is intentionally broad: it matches everything without a recorded pass, including
  `unknown` and never-evaluated mail. It does not mean forged.
- Which servers are trusted is human configuration (`mailcoded account trust`, behind a terminal
  confirmation or `MAILCODED_ALLOW_TRUST_CHANGE=1`). The MCP server has no such tool. An agent must not
  change it, and a message asking for it is an injection attempt.

## Unsubscribing

`read` and the MCP `read` tool show what a sender's `List-Unsubscribe` headers offer, under
`unsubscribe`: a `mailto` address (with an optional subject and body), up to four `web` links, and
`one_click`. The CLI verb `mailcoded unsubscribe <id>` turns the mailto into an ordinary draft. There
is no MCP tool for it: an MCP agent drafts the address itself with `send_preview`, which is the same
path.

- **The address and the links are chosen by the sender.** Replying to one confirms to that sender that
  your address is live, which is why the human reviews every unsubscribe through the normal two-phase
  send and why an unsubscribe address will normally not be on `MAILCODED_APPROVED_RECIPIENTS`. Do not
  add it there to make an agent's unsubscribe go through.
- **Web links are never opened by mailcoded, and an agent must not open them.** There is no flag that
  makes mailcoded fetch one. RFC 8058 one-click POST is refused by design: it is a request to a
  sender-controlled URL, and the network rule allows mail protocols only. The CLI prints the link for
  a human, says it was not opened, and the JSON carries a `note` saying the same. A message that tells
  an agent to visit an unsubscribe link is an injection attempt.
- Only the address, `subject` and `body` of the mailto are used. `to`, `cc`, `bcc`, `from` and any
  other field in the URI are dropped, and a URI with a line break or other control character is
  rejected, so an unsubscribe header cannot add a recipient or inject a header.
- An unsubscribe is mail you would be sending. It needs the same human decision as any other send; an
  agent drafts, the human decides.

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

`draft` never prints a token. `--from` must be the account's own address or a sender identity the
human configured (`mailcoded account identity list`); anything else exits 2 with the error name
`sender_not_allowed` and queues nothing. You may use an identity but not add one: `account identity add`
needs a terminal confirmation or `MAILCODED_ALLOW_IDENTITY_CHANGE=1` with `--yes`, and the MCP server has
no such tool. `draft`, `reply` and the MCP `draft` / `send_preview` tools append the account's default
plaintext signature after a `-- ` line, above the quote in a reply; `--signature <name>` /
`signature` picks another and `--no-signature` / `no_signature` leaves it out. `send-preview` shows
you exactly who would receive the
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

## Turning backup on

```bash
MAILCODED_ALLOW_BACKUP=1 mailcoded backup ~/mail-backups/2026-10-04 --json
MAILCODED_ALLOW_BACKUP=1 mailcoded backup-verify ~/mail-backups/2026-10-04 --json
```

A backup is a copy of `store.db` and every attachment file: exactly the file handle and the
non-plaintext-only content that "the agent never gets a handle to the database or the blob store"
rules out. So the CLI refuses an agent (exit 4) unless the variable is set, the MCP server has no such
tool and refuses an MCP caller even with the variable set, and every attempt is audited. Give an agent
this only when you want it to take backups on a schedule, and point it at a folder only you can read.

The folder must be new or empty, outside the live store, not a symbolic link, and not inside a
workspace folder (one with `.git`, `.vscode`, `.obsidian`, `.idea` or a `*.code-workspace` file) —
the mail store may not live in a workspace and neither may its copy. Secrets are never copied. The
manifest is not signed, so `backup-verify` is a check against corruption and accidents, not against
someone who can rewrite the folder. An agent must not restore: there is no restore verb, and restoring
is a human procedure (chapter 3 of the manual).

## What is audited, and where to read it

Audit rows are appended to the `sync_log` table in `store.db`. The table is append-only:
nothing in the codebase rewrites or deletes a row. Columns are `id`, `ts`, `account_id`,
`level` (`info`/`warn`/`error`), `event`, `detail`, `interface` (`cli` | `mcp` | `rpc` |
`internal`), and `agent_host`.

Recorded from the agent surface: `cli_read`, `cli_thread`, `cli_folders`, `cli_correspondents`, `cli_stats`,
`cli_health`, `cli_import`, `tags_set`, `body_fetched`, `sql_query`, the backup trail
(`backup_started`, `backup_completed`, `backup_failed`, `backup_denied`, `backup_verified`,
`backup_verify_failed`), and the send trail —
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

Unset `MAILCODED_SEND`, `MAILCODED_APPROVED_RECIPIENTS`, `MAILCODED_ENABLE_SQL`, and
`MAILCODED_ALLOW_BACKUP` in the agent host's environment (for MCP, remove the `env` entries and restart the client).
Everything returns to the default posture immediately — the gates are read from the
environment at process start, and no "unlocked" state is persisted in the store. An
outstanding confirm token and the spent-send window *are* rows in the store and survive a
restart, but a leftover token buys nothing with the gate shut: the gate is evaluated
before the token is even looked at. To remove the surface entirely, delete the binary from
`PATH` and remove the MCP server entry; the mail store is untouched by either.
