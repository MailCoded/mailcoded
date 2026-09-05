# The mailcoded manual

This is the handbook for people who read their own mail with mailcoded, from a terminal. It covers
installing it, adding an account, the full-screen client, every command-line verb, sending, and what
to do when something goes wrong.

It is deliberately not the design documentation. If you want to know *why* the daemon speaks JSON-RPC
or how the sync planner works, start at [docs/ARCHITECTURE.md](../ARCHITECTURE.md). If you are giving
an AI agent access to your mail, [docs/agents.md](../agents.md) is the document for you; chapter 11
here only points at it.

Everything in this manual was checked against **mailcoded 0.1.0, protocol 1**, on 2026-09-06, by
running the commands. Where the manual and `mailcoded help <verb>` disagree, the program is right and
the manual has a bug — please report it.

## The short version

    scripts/install.sh      # build, and put the four commands on PATH
    mailcoded setup         # add an account; nothing is saved until a real login succeeds
    mailcoded tui           # read it. Press ? for the keys.

## Contents

1. [Installing](01-install.md) — build from source, where the binaries go, checking it works
2. [Your first account](02-first-account.md) — `setup`, provider quirks, app passwords, Microsoft sign-in, scripted setup
3. [Where your mail lives](03-where-things-live.md) — the data directory, what is in it, secrets, backups, editors
4. [The terminal client](04-terminal-client.md) — `mailcoded tui`: the screen, keys, mouse, reading, triage, compose
5. [Reading and searching from the command line](05-reading-and-searching.md) — `search`, `read`, `thread`, `attachments`, `folders`
6. [Triage](06-triage.md) — Tags and Flags, `tag`, `move`, `archive`, and why there is no delete
7. [Sending](07-sending.md) — drafts, the two-phase send, the outbox, and who the send gate applies to
8. [Accounts](08-accounts.md) — `account list`, `test`, `reauth`, `add`, `forget`
9. [Sync and the daemon](09-sync-and-daemon.md) — `sync`, live updates, one daemon per store, running the daemon yourself
10. [Importing mail and raw SQL](10-import-and-sql.md) — `import-eml`, `query --sql`, reading the audit trail
11. [Agents](11-agents.md) — the two-minute version, and where the real document is
12. [The safety model, in plain terms](12-safety-model.md) — what mailcoded will never do, and why you may see �
13. [Troubleshooting](13-troubleshooting.md) — the messages you might see, what they mean, what to do
14. [Reference](14-reference.md) — environment variables, exit codes, every key, every verb, every file

## Conventions

- `mailcoded` is the command-line tool. `mailcoded-tui`, `mailcoded-daemon` and `mailcoded-mcp` are
  the other three binaries the installer puts beside it; you rarely run them directly.
- `<id>` is a local message id — the number in the first column of `search` output. It is stable within
  one store and means nothing outside it.
- The vocabulary is deliberate and the manual sticks to it: an **account** has **folders** (never
  "mailboxes"); a message carries server **Flags** (`unread`, `flagged`, `replied`, `draft`) and local
  **Tags** (never "labels").
- `--json` output is a contract, versioned by `schema_version`. The human-readable output is not, and
  may change between releases.
