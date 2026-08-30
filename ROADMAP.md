# Roadmap

mailcoded is a local-first email engine for developers and their agents.
This roadmap signals direction, not dates. Order and scope change based on real usage and issues.

## The family

| Product | What it is | Status |
|---|---|---|
| **mailcoded** | Local email backend daemon (the engine) | 🟡 In progress |
| **mailcoded** (CLI) | Thin CLI over the daemon; the agent surface | 🟡 In progress |
| **mailcoded for VS Code** | Read, search, and triage mail in your editor | 🟡 In progress |
| MCP adapter | `mailcoded --mcp` for agent hosts without a shell | ⚪ Planned (M5) |
| Microsoft Graph provider | Work mail after the EWS shutdown | ⚪ Planned (v0.2) |
| HTTP / socket API | `mailcoded --listen` — unlocks every other client | ⚪ Planned (v0.2) |
| Obsidian plugin | Email in your PKM vault | ⚪ Planned (v0.3) |
| Docker image | Always-on headless sync | ⚪ Planned (v0.4) |
| Web / PWA | Thin remote triage window | 🔵 Exploring |

## Principles

- **Local-first.** Your mail and your index stay on your machine. No telemetry, no ads, no cloud round-trip to search your own inbox.
- **Plaintext-first.** HTML mail renders sandboxed with remote content blocked by default. Tracking pixels don't load.
- **Agent-safe by construction.** No delete tool exists for agents — not gated, absent. Sending requires a human-confirmed one-time token, a recipient allowlist, and a rate limit, and every attempt is logged.
- **One backend, many clients.** The engine is a daemon with a documented JSON-RPC surface. Editors, plugins, and agents are all just clients.
- **One transport at a time. One new client at a time.**

## Entry gates (why things aren't built yet)

- A second client ships only after the HTTP/socket transport is hardened (bearer token, Host/Origin allowlist, localhost-only bind).
- Obsidian ships after the VS Code extension shows real traction.
- Docker ships when enough of you ask for always-on sync.
- Web ships only if Docker users need remote or mobile access — Roundcube and SnappyMail already exist for general webmail.

## Known gaps (deliberate, for now)

- **No attachment-content search.** We index subject and body, not text inside PDFs/Office files. On the roadmap.
- **Windows needs no external tools**, but the classic Unix stack (mbsync/notmuch) interop is POSIX-only.
- **No calendar, contacts, or PGP.** Not planned.

## Request something

Open an issue with your use case. Demand moves items up this list.
