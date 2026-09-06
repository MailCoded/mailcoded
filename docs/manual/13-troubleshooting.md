# 13. Troubleshooting

[← Contents](./)

Start with these three; between them they explain most problems:

    mailcoded health          # store, secret backend, gates, per-account state
    mailcoded account test    # does the credential still work? IMAP and SMTP, separately
    mailcoded stats           # counters, the outbox, sends left this hour

In the TUI, `S` shows the same on one screen, including each account's last error, and `A` runs the
connection test.

## Messages, and what to do about them

### Getting started

| You see | It means | Do |
|---|---|---|
| `No account is configured. Run 'mailcoded setup' first.` | the store has no accounts | chapter 2 |
| `'setup' is interactive and needs a terminal.` | `setup` was run from a script, or with redirected input | use `account add --password-stdin` |
| `'mailcoded-tui' was not found beside this binary or on PATH.` | the TUI binary is not installed next to the CLI | `scripts/install.sh`, or run `mailcoded-tui` from the build output directly (chapter 1) |
| `The TUI needs a terminal; stdin or stdout is redirected.` | `mailcoded tui` inside a pipe | run it in a terminal; scripts use the one-shot verbs |
| `This console cannot render ANSI. Try Windows Terminal.` (exit 2) | the legacy Windows console | use Windows Terminal |

### Signing in

| You see | It means | Do |
|---|---|---|
| a login failure from `setup`, with hints | wrong host or port, or the provider wants an app password | read the hints; most providers reject the account password over IMAP (chapter 2) |
| `Microsoft did not return a sign-in code within 45s. Check the network, then try again.` | the device-code request did not complete | check connectivity to `login.microsoftonline.com`; try again |
| the Microsoft consent page names another application | mailcoded borrows a public client registration | expected; use `--client-id` with your own registration if you prefer (chapter 2) |
| `account test` reports IMAP ok but SMTP `auth` | you can read; the SMTP server rejected the credential | sending fails until it accepts; reading is unaffected. For Microsoft accounts this is a known open issue at the time of writing |
| `The new credential was stored but the server still refuses it: ...` | `reauth` stored it; the server said no | the reason follows; exit 7 is authentication, 8 is network |

### Syncing

| You see | It means | Do |
|---|---|---|
| `sync: Timed out connecting to <host>:<port>.` | nothing answered at that address | a wrong host or a firewall. An account with no credential is refused before this point, so a timeout means the settings are wrong rather than the credential |
| `This account has no stored credential, so it cannot sync. ...` | watching an account registered without a credential, or the local import account | `mailcoded account reauth --account <id>`, or leave it |
| `auth (1000): No stored credential for <address>.` | the same account, on an explicit `sync`, `read` or `account test` | as above. It is refused before the connection is attempted, so it fails at once rather than after a timeout |
| `Live updates belong to another mailcoded window; press r to refresh here.` | another daemon owns this store's live connections | normal with two TUI windows; `r` refreshes (chapter 9) |
| `Another live daemon (pid N) owns this store; watch connections stay closed here.` (stderr) | the same, from the daemon's side | |
| any other red `sync: ...` | the server refused, or failed | `A` or `account test` for the detail; `S` for the last error |
| `Still <something>. Press esc to give up on it.` | a server operation is slow | wait, or `esc` to cancel it |
| `The daemon is gone: ...`, TUI exits 3 | the daemon process died | the TUI prints the daemon's recent stderr as it exits; run it again; `mailcoded-tui --check` |

### Reading

| You see | It means |
|---|---|
| `(body not fetched)` in the reader | the body could not be downloaded just now |
| `(this message has no plaintext part)` | it is HTML-only; there is no HTML view |
| `�` in a subject or body | the message contained control or invisible characters (chapter 12) |
| `matches were dropped that no cursor reaches` | a search hit the paging ceiling; narrow it, or order by date |

### Sending

| You see | It means | Do |
|---|---|---|
| `Sending from the agent surface requires MAILCODED_SEND=1.` (exit 4) | the CLI send gate is closed | chapter 7 — or send from the TUI, which is not gated this way |
| exit 6, `confirm-required` | no valid token: missing, spent, expired, or the draft changed | `send-preview` again |
| `That confirmation is spent or expired. Preview again.` (TUI) | the same | `ctrl-s` again |
| exit 5, `rate-limited` | five sends this hour already | wait `retry_after_ms` |
| a recipient refused | not on `MAILCODED_APPROVED_RECIPIENTS` | add it, or send from the TUI |
| `outbox` shows `failed` with an SMTP reply | the server rejected the message | the reply says why |

### Moving and removing

| You see | It means | Do |
|---|---|---|
| `Moving mail is outside the default agent posture. ...` | `move` or `archive` from a script, without `MAILCODED_ALLOW_MOVE=1` and `--yes` | set the variable; add `--yes` for an unattended run |
| `Moving mail is not part of the agent surface.` (exit 4) | you confirmed at the terminal, but `MAILCODED_ALLOW_MOVE` is not set — the CLI is an agent surface | `MAILCODED_ALLOW_MOVE=1 mailcoded move ...`, or use `m` in the TUI |
| `This account has no folder marked as Archive.` | the server exposes no Archive role | `move --folder <name>` |
| `No folder called 'X'. This account has: ...` | a typo, or a folder that has not synced yet | pick from the list; `sync` |
| `A message can only move within its own account.` (TUI) | you picked a folder under another account | pick one under the same account |
| you are looking for delete | there is none, by design | chapter 6 |

### Raw SQL

| You see | Do |
|---|---|
| `Raw SQL reads require MAILCODED_ENABLE_SQL=1.` (exit 4) | set it, or use `search`, `read` and `thread` |

## Exit codes

| Code | | Retry? |
|---|---|---|
| 0 | success | |
| 1 | internal error | report it |
| 2 | validation — bad arguments | fix the command |
| 3 | not found — no such message, folder, account or draft | |
| 4 | forbidden — a safety gate refused | no; open the gate deliberately, or don't |
| 5 | rate limited | after `retry_after_ms` |
| 6 | confirm required — send needs a valid one-time token | preview again |
| 7 | auth — the credential failed or is missing | `account reauth` |
| 8 | network — transient | yes, with backoff |
| 9 | store — the database is corrupt or full | |
| 10 | unsupported — the server or this build lacks a capability | |
| 130 | cancelled | |

## Getting more detail

- The daemon logs to stderr. When the TUI exits because the daemon died it prints what the daemon
  said; to drive the daemon by hand, `mailcoded-daemon --log-level debug`.
- `mailcoded health --json` and `mailcoded stats --json` are the complete pictures.
- `mailcoded-tui --check` proves the wire without a screen.

## Starting over

A fresh sync from the server is always valid. `mailcoded account forget --account <id>` removes the
local copy — including your local Tags and drafts for that account — and `mailcoded setup` re-adds
it. If the store itself is damaged (exit 9), move the data directory aside and set up again; the
server still has your mail.
