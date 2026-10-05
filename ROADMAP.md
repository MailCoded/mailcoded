# Roadmap

mailcoded is a local-first email engine for developers and their agents.
This roadmap signals direction, not dates. Order and scope change based on real usage and issues.

## The family

| Product | What it is | Status |
|---|---|---|
| **mailcoded** | Local email backend daemon (the engine) | 🟡 In progress |
| **mailcoded** (CLI) | In-process CLI over the engine; the agent surface | 🟡 In progress |
| `mailcoded-tui` | Reference client over JSON-RPC; not a supported product | 🟡 In progress |
| **mailcoded for VS Code** | Read, search, and triage mail in your editor | 🟡 In progress |
| MCP adapter | `mailcoded-mcp`, a separate binary for agent hosts without a shell | 🟡 In progress |
| Microsoft Graph provider | Work mail after the EWS shutdown | ⚪ Planned (v0.2) |
| HTTP / socket API | `mailcoded --listen` — unlocks every other client | ⚪ Planned (v0.2) |
| Obsidian plugin | Email in your PKM vault | ⚪ Planned (v0.3) |
| Docker image | Always-on headless sync | ⚪ Planned (v0.4) |
| Web / PWA | Thin remote triage window | 🔵 Exploring |

## Principles

- **Local-first.** Your mail and your index stay on your machine. No telemetry, no ads, no cloud round-trip to search your own inbox. Signing in to a Microsoft account talks to Microsoft's identity service and nothing else does. Search by meaning, when it is finished, will run its model locally: the installer will be able to fetch one on request, and no model is pinned yet, so today it fetches nothing.
- **Plaintext-first.** HTML mail renders sandboxed with remote content blocked by default. Tracking pixels don't load.
- **Agent-safe by construction.** No delete tool exists for agents — not gated, absent. Sending requires a human-confirmed one-time token, a recipient allowlist, and a rate limit, and every attempt is logged.
- **One backend, many clients.** The engine is a daemon with a documented JSON-RPC surface. Editors, plugins, and agents are all just clients.
- **One transport at a time. One new client at a time.**

## Added to the engine by the owner's decision

Built in the backend ahead of the order above, each with unit tests and none yet exercised against a
live mail server (`docs/verification.md` says exactly what has and has not run). The principles above
did not change; every item below is read-only or gated for agents and adds no delete verb.

- **Message signals** — `is:automated`, `is:encrypted`, `is:verified` (DMARC pass from a server you chose to trust), and four local category Tags. They sort mail and never vouch for it.
- **Search that skips Trash and Junk** unless you name a Folder or say `in:anywhere`.
- **Attachment text search**, narrowly: text-like files and Word `.docx` through `attachment:`; PDFs, spreadsheets, presentations and images are still unread.
- **`backup` and `backup-verify`** — a verifiable copy of the store; never secrets; off for agents unless you unlock it.
- **Sync progress** in the CLI and the terminal client, from a new `notify.sync.progress` notification.
- **Folder hierarchy** on the wire, in `folders` and in the terminal sidebar.
- **Sender identities and plaintext signatures**, and **drafts saved to the server's Drafts Folder** (opt-in, never deleted).
- **Compose suggestions** learned from the mail you sent, never from the mail you received.
- **`unsubscribe`** — drafts the sender's mailto; never opens a link and never POSTs one.
- **An interactive IMAP connection** so a fetch or a move no longer waits behind a long sync.
- **A 100,000-message read-path test**, opt-in, run once on a development machine.

Not built: a **native Gmail REST provider**. The network rule allows mail protocols and the Microsoft
identity endpoints only, and Graph comes first in v0.2. Gmail works over IMAP with an app password.

## Entry gates (why things aren't built yet)

Every client in the family has a repository from the start, so the shape of the thing is legible
and the constraints each client inherits are written down where that client's author will look.
A repository existing is not a claim that work has begun — each placeholder says plainly that it
is one, and names the gate it is waiting on. Code is what is gated, not folders.

- A second client ships only after the HTTP/socket transport is hardened (bearer token, Host/Origin allowlist, localhost-only bind).
- Obsidian ships after the VS Code extension shows real traction.
- Docker ships when enough of you ask for always-on sync.
- Web ships only if Docker users need remote or mobile access — Roundcube and SnappyMail already exist for general webmail.
- **One in flight at a time.** Exactly one client is under active development; the rest stay placeholders.

## Known gaps (deliberate, for now)

- **Attachment search covers text-like files and Word `.docx` only.** Text inside PDFs, spreadsheets, presentations and images is not indexed. On the roadmap.
- **Thread view and full-text search on a very common word are above their targets at 100,000 messages** (one run on a development machine, not reference hardware). Both need an index or a query change; neither is done.
- **Windows needs no external tools**, but the classic Unix stack (mbsync/notmuch) interop is POSIX-only.
- **No calendar, contacts, or PGP.** Not planned.

## Request something

Open an issue with your use case. Demand moves items up this list.
