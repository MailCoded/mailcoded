# mailcoded

**Local-first email in your editor — your mail, full-text search, no tracking.**

*The mu4e/aerc experience for people who live in VS Code.*

mailcoded is a cross-platform mail **engine**: a .NET 10 daemon that syncs an IMAP account into a
local SQLite store with FTS5 full-text search and notmuch-style Tags, sends via SMTP behind a
two-phase confirmation, and exposes the whole thing over JSON-RPC 2.0 on stdio. One backend, many
clients — an editor extension, a CLI, and an MCP adapter are all just clients of the same daemon.

Your mail and your index live on your machine. There is no telemetry, no analytics, and no network
call to anything except your own mail servers.

> **Status: pre-release.** The backend (Core, daemon, CLI, MCP adapter) builds and runs. There is
> no tagged release and no published binary yet, so everything below builds from source. The VS
> Code extension is not built yet. See [Known gaps](#known-gaps).

## It's a sidecar, not a migration

Point mailcoded at the same IMAP account you already read in Outlook, Apple Mail, or Thunderbird.
It syncs a local copy; it does not take over your mail, it does not need to be your only client,
and it does not ask you to change anything about how your mail is delivered. Run it alongside
whatever you use today, and stop if you don't like it — your mail was never anywhere else.

Two consequences of that framing are load-bearing:

- **The server stays the source of truth for mail.** Sync is idempotent and re-runnable, the server
  wins on Flags, and re-syncing from scratch is always a valid recovery path.
- **Nothing is destructive.** There is no delete, expunge, trash or purge command anywhere in the
  CLI, the MCP surface, or the JSON-RPC protocol. Not gated — absent.

## Install

No release binaries yet. Build from source:

```bash
git clone https://github.com/Mailcoded/mailcoded
cd mailcoded
dotnet build Mailcoded.slnx           # or: scripts/build.sh
```

Requires the .NET 10 SDK. On NixOS/WSL, `nix develop` first.

The three executables are `mailcoded` (CLI), `mailcoded-daemon` (JSON-RPC daemon), and
`mailcoded-mcp` (MCP adapter). Native AOT single-file publishing is wired up and exercised in CI:

```bash
dotnet publish src/Mailcoded.Daemon -c Release -r linux-x64 /p:PublishAot=true
```

## Quick start

Steps 1 and 2 are verified working against this tree — all 35 bundled fixtures import with zero
failures, and the search examples return hits. The rest are the documented verbs; run
`mailcoded help <verb>` for the authoritative arguments.

```bash
# 1. Load some mail into a store without touching a server at all.
mailcoded --db /tmp/mail.db import-eml fixtures/eml --json

# 2. Search it. Local only; nothing goes over the network.
mailcoded --db /tmp/mail.db search 'invoice' --json
mailcoded --db /tmp/mail.db search 'tag:unread after:2026-01-01' --json --order date
mailcoded --db /tmp/mail.db search 'from:acme has:attachment' --json --limit 20

# 3. Read one message. Plaintext, always. The id comes from a search hit.
mailcoded --db /tmp/mail.db read 4213 --plaintext

# 4. Where things stand.
mailcoded --db /tmp/mail.db stats --json
mailcoded --db /tmp/mail.db health --json
```

Search understands bare words and phrases plus `from:`, `to:`, `cc:`, `subject:`, `tag:`,
`folder:`, `is:unread|flagged|draft|replied`, `has:attachment`, `before:`/`after:`, and `-`
negation. Diacritics fold, and CJK is indexed with a trigram route (with a `LIKE` fallback for one-
and two-character terms).

Add a real account — the password is read from stdin and goes straight to the OS keyring or the
encrypted-file vault, never into the database, config, or a log line:

```bash
printf '%s' "$IMAP_PASSWORD" | mailcoded account add --json \
  --email me@example.com \
  --imap-host imap.example.com \
  --smtp-host smtp.example.com \
  --password-stdin

mailcoded sync --json
mailcoded folders --json
```

Run `mailcoded help` for the full verb list, and `mailcoded help <verb>` for arguments and
examples.

The daemon speaks Content-Length-framed JSON-RPC 2.0 on stdio:

```bash
mailcoded-daemon --store /tmp/mail.db
```

`initialize`, `account.list`, `folder.list`, `stats`, `health` and `shutdown` are verified working
over that transport. The full protocol reference — every method, every notification, the framing
with a copyable worked example, capability negotiation, and every numeric error code with what a
client should do about it — is in **[docs/rpc.md](docs/rpc.md)**.

## Architecture

One core, several thin hosts. `Mailcoded.Core` holds a pure `Domain` (sync planner, threading,
Tag↔Flag mapping — no I/O, no clock, no logging), an `Application` layer that owns every safety
gate, and adapters for `Providers` (MailKit), `Parsing` (MimeKit), `Store` (SQLite + FTS5) and
`Secrets`. MailKit and MimeKit types never escape their adapters. The hosts — daemon, CLI, MCP —
contain no business logic; they are transports. SQLite is the single source of truth on every
platform, writes go through one writer thread in single transactions, and the daemon is long-lived
so IMAP connection state (IDLE, auth) amortizes across calls the way an LSP server amortizes a
project index.

```
+---------------------------+     stdio JSON-RPC 2.0      +---------------------------+
|  VS Code extension        | <-------------------------> |  mailcoded (daemon)       |
|  - TreeView sidebar       |                             |  .NET 10, Native AOT      |
|  - Webview reader (CSP)   |    +------------------+     |  +---------------------+  |
|  - Compose buffer         |    | mailcoded-mcp    | --> |  | Mailcoded.Core      |  |
+---------------------------+    +------------------+     |  |  Domain (pure)      |  |
                                                          |  |  Application        |  |
+---------------------------+     one-shot RPC            |  |  Providers  Parsing |  |
|  mailcoded (CLI)          | --------------------------> |  |  Store      Secrets |  |
+---------------------------+                             |  +---------------------+  |
                                                          +---------------------------+
                                                            IMAP / SMTP / Graph
```

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md),
[docs/RELIABILITY.md](docs/RELIABILITY.md), [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md).

## For agents

This is the second story, not the headline. mailcoded is a mail client first; the agent surface is
what falls out of having a good CLI.

Shell-capable agents (Claude Code, Codex CLI, Gemini CLI, opencode, Goose) use the CLI directly —
`mailcoded search '<query>' --json` — which costs essentially no tokens compared to a tool-schema
surface. Hosts that cannot run a shell get `mailcoded-mcp`, a thin adapter whose tools map 1:1 to
CLI verbs. Both go through the same Core, so the same gates apply.

Incoming email is attacker-controlled input, and an agent with mail access otherwise holds all
three legs of the lethal trifecta — private data, untrusted content, external communication. So:

- **No delete tool exists.** In any layer. Absent, not gated.
- **Agents get plaintext bodies only**, never HTML, which kills markdown- and image-based
  exfiltration.
- **Sending requires** `MAILCODED_SEND=1`, a two-phase `send-preview` → one-time-token exchange,
  every recipient matching `MAILCODED_APPROVED_RECIPIENTS`, at most 5 sends per rolling hour, and
  an audit row per attempt whether allowed or denied. The human-reviewed preview *is* the
  supervision.
- **Raw SQL reads** are off unless `MAILCODED_ENABLE_SQL=1`, and are then read-only, single-
  statement, and row-capped. Agents never get a file handle to the database, the blob store, or a
  Maildir.
- **All gates live in Core**, never in an adapter, so no host can route around them.

Design and rationale: [docs/AGENT-INTERFACE.md](docs/AGENT-INTERFACE.md).

## Security posture

- Credentials go to the OS keyring (Windows Credential Manager / macOS Keychain / libsecret) with a
  required encrypted-file fallback for headless and container use. They never appear in the
  database, in `config_json`, in logs, in RPC responses, or in exception messages.
- `message.get` returns `bodyHtml` **raw and unsanitized** — sanitizing it is the client's job,
  because only the client knows its rendering context. See the warning in
  [docs/rpc.md](docs/rpc.md).
- The planned VS Code reader renders HTML only inside a sandboxed webview, after DOMPurify, under
  a CSP with **no remote origins**. Remote images — including tracking pixels — are blocked by
  default with a per-message opt-in.
- No S/MIME, no PGP, no BouncyCastle anywhere in the tree.
- The extension will never open a network connection to a mail server. Only the daemon does.

## Performance

The data layer is designed for 500k+ messages: contentless FTS5, covering indexes, keyset-seek
paging (never `OFFSET`), prepared-command reuse, synchronous SQLite on a dedicated writer thread,
and deferred index writes during backfill.

**These are targets, not measurements. No number below has been recorded on reference hardware, so
please do not quote them as results.**

| Operation | Target |
|---|---|
| Envelope page (50 rows @ 500k) | < 10 ms |
| FTS search @ 500k docs | < 100 ms p95 |
| Unread badge | < 5 ms |
| Thread view | < 20 ms |
| Initial ingest | ≥ 2,000 envelopes/sec |

The BenchmarkDotNet harness that will produce the real numbers lives in
[`tests/Mailcoded.Bench`](tests/Mailcoded.Bench) — fixed-seed 500k synthetic corpus (~10% CJK),
gates on absolute budgets plus a 15%-regression rule against a committed baseline. It has **not**
been run on a reference machine: `baseline.json` currently ships `p95Ns: 0` for every benchmark,
which the gate reports as "not baselined" rather than silently passing. When a reference machine is
recorded — exact CPU, RAM, storage, OS build, .NET version — this section gets numbers and a link
to the methodology, and not before.

```bash
dotnet run -c Release --project tests/Mailcoded.Bench
```

Budget rationale and query shapes: [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## Platform matrix

| | Build + unit tests | AOT publish + stdio smoke | Secret backend | Notes |
|---|---|---|---|---|
| Linux x64 | CI | CI (`linux-x64`) | libsecret, encrypted file | primary development platform |
| Windows x64 | CI | CI (`win-x64`) | Credential Manager, DPAPI file | see the Windows notes below |
| macOS arm64 | CI | CI (`osx-arm64`) | Keychain, encrypted file | |
| macOS x64 | not in CI | not in CI | Keychain, encrypted file | should work; untested |
| Linux arm64 | not in CI | not in CI | libsecret, encrypted file | should work; untested |
| Headless / Docker | — | — | encrypted file only | the file fallback is required, not optional |

Integration tests (Dovecot + smtp4dev via Testcontainers) need Docker and are skipped when it is
unavailable.

## Editor configuration

The mail store must **never** live inside a workspace folder, and no editor should watch or index
it. If a store directory is ever visible to your editor, exclude it — a 500k-message SQLite
database plus a WAL will otherwise be re-indexed on every write.

```jsonc
{
  "files.watcherExclude": {
    "**/mailcoded/**": true,
    "**/*.db": true,
    "**/*.db-wal": true,
    "**/*.db-shm": true
  },
  "search.exclude": {
    "**/mailcoded/**": true,
    "**/*.db": true,
    "**/*.db-wal": true,
    "**/*.db-shm": true
  }
}
```

Default store roots: `%LOCALAPPDATA%\mailcoded` on Windows,
`~/Library/Application Support/mailcoded` on macOS, `$XDG_DATA_HOME/mailcoded` (usually
`~/.local/share/mailcoded`) on Linux. Override with `--data-dir`, `--db`, or `MAILCODED_DATA_DIR`.

### Windows notes

- **Enable long paths.** The store root is kept deliberately short because of `MAX_PATH`, but blob
  and export paths can still get long. Set
  `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` to `1` (a reboot applies it),
  or enable *Computer Configuration → Administrative Templates → System → Filesystem → Enable Win32
  long paths* in Group Policy.
- **Consider a Defender exclusion** for the store root (`%LOCALAPPDATA%\mailcoded`). Real-time
  scanning of a busy SQLite database and its WAL costs a lot of sync throughput. This is optional
  and it is your security tradeoff to make — mail *content* is what Defender would be scanning, so
  weigh it accordingly. Add it with:
  `Add-MpPreference -ExclusionPath "$env:LOCALAPPDATA\mailcoded"`.
- Maildir's `:` separator is illegal on NTFS, so Windows export writes the `!` variant and never
  `:` on a Windows path. Folder names are case-preserving and compared case-insensitively.

## Known gaps

Stated plainly, because these are the things you would otherwise discover on day three:

- **No attachment-content search.** We index subject and body text only. Text inside PDFs, Word
  documents, and spreadsheets is **not** searchable, and no phrasing anywhere in this project should
  suggest otherwise. On the roadmap; not in v0.1.
- **No calendar and no contacts.** Not planned.
- **No PGP and no S/MIME.** Out of scope, and deliberately excluded from the Native AOT path.
- **IMAP only in v0.1.** Microsoft Graph — the durable path for work mail once EWS is disabled on
  1 Oct 2026 — is planned for v0.2, along with Gmail OAuth2 and multi-account.
- **The VS Code extension is not built yet.** The daemon, its protocol, and the CLI exist; the
  extension is the next milestone and ships from a separate repository.
- No POP3, no JMAP (v0.3+), no HTML compose, no attachment upload in the composer, no message-rules
  engine, no IMAP server mode, no mobile or web client.
- No release binaries yet, and therefore no measured performance numbers. Build from source.

Direction, and why some things are deliberately not built yet: [ROADMAP.md](ROADMAP.md).

## Documentation

| | |
|---|---|
| [SPEC.md](SPEC.md) | the executable specification |
| [docs/rpc.md](docs/rpc.md) | JSON-RPC reference: methods, framing, error codes |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | layering, dependency direction, error policy |
| [docs/AGENT-INTERFACE.md](docs/AGENT-INTERFACE.md) | the agent surface and its threat model |
| [docs/RELIABILITY.md](docs/RELIABILITY.md) | sync edge cases, backoff, recovery |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | data-layer budgets and query shapes |
| [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md) | the closed dependency allowlist |
| [ROADMAP.md](ROADMAP.md) | what's next, and what isn't |

## Contributing

Read `SPEC.md` and `CLAUDE.md` first; the invariants in `CLAUDE.md` are not negotiable and win over
everything else. Dependencies are a closed allowlist. `fixtures/eml/` is append-only: when a real
message breaks parsing, add a minimized, anonymized fixture that reproduces it *before* fixing it.

```bash
dotnet build Mailcoded.slnx
dotnet test tests/Mailcoded.Core.Tests
dotnet test tests/Mailcoded.Integration     # needs Docker
```

## License

MIT. The `LICENSE` file lands with the first tagged release.
