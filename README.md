# mailcoded

**Local-first email in your editor — your mail, full-text search, no tracking.**

*The mu4e/aerc experience for people who live in VS Code.*

mailcoded is a cross-platform mail **engine**: a .NET 10 daemon that syncs an IMAP account into a
local SQLite store with FTS5 full-text search and notmuch-style Tags, sends via SMTP behind a
two-phase confirmation, and exposes the whole thing over JSON-RPC 2.0 on stdio. One backend, many
clients — an editor extension, a CLI, and an MCP adapter are all just clients of the same daemon.

Your mail and your index live on your machine. There is no telemetry and no analytics. The daemon,
CLI, TUI and MCP adapter talk to your own mail servers, and — only when you sign in to a Microsoft
account — to Microsoft's identity service. Nothing else. The installer can fetch an embedding model
for search-by-meaning if you ask it to, but no model is pinned yet, so today it fetches nothing.

> **Status: pre-release.** The backend (Core, daemon, CLI, MCP adapter) builds and runs. There is
> no tagged release and no published binary yet, so everything below builds from source. The VS
> Code extension is unreleased and lives in its own repository. See [Known gaps](#known-gaps).

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
git clone https://github.com/MailCoded/mailcoded
cd mailcoded
scripts/install.sh                    # publishes, then puts the commands on PATH
```

Requires the .NET 10 SDK. On NixOS/WSL, `nix develop` first.

That installs four commands: **`mailcoded`** (CLI), **`mailcoded-tui`** (terminal client),
**`mailcoded-daemon`** (JSON-RPC daemon) and **`mailcoded-mcp`** (MCP adapter). Without this step
`dotnet build` alone leaves the binaries under `src/*/bin/` and nothing is on your PATH, so none of
the commands below will resolve.

```bash
scripts/install.sh                       # Native AOT, into ~/.local
scripts/install.sh --no-aot              # much faster; needs the .NET runtime at run time
scripts/install.sh --prefix /usr/local   # somewhere else
scripts/install.sh --uninstall           # remove it again
```

The payload goes to `$PREFIX/libexec/mailcoded` and only symlinks land in `$PREFIX/bin`. Two
reasons, both load-bearing: the AOT binaries load `libe_sqlite3.so` from their own directory and
will not start without it, so the executable and the library must stay together; and
`$PREFIX/share/mailcoded` is *not* available for this, because on Linux that path is
`$XDG_DATA_HOME/mailcoded` — the mail store itself. The installer refuses to write over or
delete any directory containing `store.db`, `blobs` or `secrets.vault`.

To build without installing:

```bash
scripts/build.sh                      # or: dotnet build Mailcoded.slnx -m:1
scripts/test.sh                       # the unit suite
dotnet publish src/Mailcoded.Daemon -c Release -r linux-x64 /p:PublishAot=true
```

It is **not a single binary**, and nothing here should call it one. Sizes come from the publish
output rather than an estimate, measured 2026-09-12 on linux-x64:

| Binary | Size | Build |
|---|---|---|
| `mailcoded-daemon` | **16.25 MiB** | Native AOT, zero trim or AOT warnings |
| `mailcoded` (CLI) | **15.53 MiB** | Native AOT, zero trim or AOT warnings |
| `mailcoded-tui` | **5.90 MiB** | Native AOT, zero trim or AOT warnings |
| `mailcoded-mcp` | **64.11 MiB** | framework-dependent; needs the .NET runtime |

The MCP adapter is the odd one out because its SDK is not annotated for trimming, so it cannot be
compiled AOT. Most of its bulk is a `runtimes/` tree carrying SQLite natives for thirty platforms,
because a runtime-independent publish cannot know which one it will run on. Each AOT binary ships alongside `libe_sqlite3.so` (1.40 MiB) and will not start without
it. Desktop RIDs get only the shared library from the SQLite native package — there is no static
`e_sqlite3.a` to link — so distribute the pair. Details in
[docs/DEPENDENCIES.md](docs/DEPENDENCIES.md).

## Quick start

Steps 1 and 2 are verified working against this tree — all 37 bundled fixtures import with zero
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

# 5. Or browse it in a full-screen terminal client. Press ? for the keys.
mailcoded --db /tmp/mail.db tui
```

`mailcoded tui` starts `mailcoded-tui`, which spawns `mailcoded-daemon` and talks to it over
JSON-RPC rather than opening the store itself. It is the worked example behind `docs/rpc.md`: it
compiles against `Mailcoded.Protocol` alone, which is why it is 5.90 MiB where the CLI is 15.53 MiB.

Search understands bare words and phrases plus `from:`, `to:`, `cc:`, `subject:`, `tag:`,
`folder:`, `is:unread|flagged|draft|replied`, `has:attachment`, `before:`/`after:`, and `-`
negation. Diacritics fold, and CJK is indexed with a trigram route (with a `LIKE` fallback for one-
and two-character terms). `--meaning` additionally ranks by what a message is about rather than
which words it used, if you installed a model — see [Known gaps](#known-gaps) for its real state.

Add a real account:

```bash
mailcoded setup
```

That is interactive and does the work for you: it resolves your provider's IMAP and SMTP settings
from your address, tells you up front if that provider needs an **app password** rather than your
account password (Gmail, Yahoo, iCloud, Fastmail, QQ and others do, and will simply reject the
wrong one), proves the settings work with a real login, and only then saves anything. A failed
attempt leaves no account and no stored credential behind.

Settings are built in for Gmail, Outlook.com, Yahoo, iCloud, Fastmail, AOL, Zoho, GMX, WEB.DE,
Yandex, Mail.ru, QQ, Foxmail, NetEase and Proton Bridge; any other domain is guessed as
`imap.<domain>` / `smtp.<domain>` and you can correct it when asked. Microsoft accounts are offered
*Sign in with Microsoft* — a device-code flow — alongside a password, because Microsoft has disabled
basic authentication for most of them.

The password is typed at a prompt, never echoed, and never a command-line argument, so it cannot
reach your shell history. It goes to the OS keyring (or the encrypted-file vault) — never to the
database, the config, or a log line.

For scripts and CI, the non-interactive form takes every setting as an option and reads the
credential from stdin:

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
- The VS Code reader renders HTML only inside a sandboxed webview, after DOMPurify, under
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
gates on absolute budgets plus a 15%-regression rule against a committed baseline.

**The suite has never been run on reference hardware.**
`baseline.json` is committed with `p95Ns: 0` for every benchmark deliberately, so the gate reports
"not baselined" rather than silently passing. When a reference machine is recorded — exact CPU,
RAM, storage, OS build, .NET version — this section gets numbers and a link to the methodology,
and not before.

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
  1 Oct 2026 — is planned for v0.2, along with Gmail OAuth2.
- **The VS Code extension is unreleased and lives in a separate repository.** It reads mail there —
  sidebar, message list, sandboxed reader, search — but it has no account setup and no compose or
  send, there is no VSIX on any marketplace, and nothing in this repository tests it.
- **Search-by-meaning is built but unproven.** The encoder, the vector store, the background
  backfill and the rank fusion all exist and run end to end. But **no model is pinned** — the
  installer ships with an empty URL and checksum and refuses to download until a human has read a
  licence and recorded a digest — and no recall harness has been built, so it has never been shown
  to return better results than plain full-text search. Treat it as unfinished, not as a feature.
- No POP3, no JMAP (v0.3+), no HTML compose, no attachment upload in the composer, no message-rules
  engine, no IMAP server mode, no mobile or web client.
- No release binaries yet, and therefore no measured performance numbers. Build from source.

What has actually been run against the real binaries, and what has not: [docs/verification.md](docs/verification.md). Direction, and why some things are deliberately not built yet: [ROADMAP.md](ROADMAP.md).

## Documentation

| | |
|---|---|
| [docs/manual/](docs/manual/README.md) | **the user manual**: installing, accounts, the terminal client, every verb, troubleshooting. Published at [mailcoded.github.io/mailcoded](https://mailcoded.github.io/mailcoded/) |
| [SPEC.md](SPEC.md) | the executable specification |
| [docs/rpc.md](docs/rpc.md) | JSON-RPC reference: methods, framing, error codes |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | layering, dependency direction, error policy |
| [docs/AGENT-INTERFACE.md](docs/AGENT-INTERFACE.md) | the agent surface and its threat model |
| [docs/RELIABILITY.md](docs/RELIABILITY.md) | sync edge cases, backoff, recovery |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | data-layer budgets and query shapes |
| [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md) | the closed dependency allowlist |
| [docs/verification.md](docs/verification.md) | what has been run, and what must not be claimed |
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

MIT — see [LICENSE](LICENSE).
