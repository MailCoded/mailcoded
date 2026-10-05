# mailcoded integration suite

Testcontainers-backed tests against a real Dovecot IMAP server and a real smtp4dev SMTP sink.
They cover the SPEC §8 acceptance path and the M2 acceptance criteria that only a server can prove.

## Running

```bash
dotnet test tests/Mailcoded.Integration                       # everything
dotnet test tests/Mailcoded.Integration --filter-trait "scenario=end-to-end"
dotnet test tests/Mailcoded.Integration --filter-trait "scenario=idle"
```

Every test carries `integration=docker`; each also names its scenario:

| Trait | Test | Covers |
|---|---|---|
| `scenario=end-to-end` | `EndToEndTests` | account → sync → search → tag → flag on the server → send → APPEND to Sent |
| `scenario=uidvalidity` | `UidValidityTests` | edge case 1: invalidate, re-enumerate, no duplicate blob rows |
| `scenario=crash-recovery` | `CrashReconciliationTests` | crash between SMTP 250 and the DB commit; never sends twice |
| `scenario=idle` | `IdleNotificationTests` | external APPEND → `notify.mail.added` within 5 s, through the real daemon |
| `scenario=signals` | `MessageSignalsTests`, `ListUnsubscribeTests` | the envelope FETCH asks for the signal headers: `is:automated`, `is:encrypted` and, through a trusted authserv-id, `is:verified` after a plain sync; the folded `List-Unsubscribe` and `-Post` headers are stored with the envelope |
| `scenario=progress` | `SyncProgressTests` | `notify.sync.progress` through the real daemon: an opted-in Folder sync and an account sync open and close their indicators before the response; a sync that did not ask gets none; a watched Folder reports before `notify.mail.added` |
| `scenario=identities` | `SenderIdentityTests` | a draft sent as a configured identity reaches the SMTP sink and the Sent copy with that From and its signature, in any case of the address; a From the account does not own is refused and queues nothing |
| `scenario=server-drafts` | `ServerDraftTests` | an opted-in draft is APPENDed to Drafts with `\Draft` and `\Seen` and carries the outbox Message-ID; after the send it is moved into Sent with one MOVE (one Sent copy, `\Draft` cleared, nothing flagged deleted); after a UIDVALIDITY change the draft is left in Drafts and a normal Sent copy is appended |
| `scenario=correspondents` | `CorrespondentTests` | a send and its synced Sent copy teach a recipient once; mail another client left in Sent teaches its recipient; mail in the Inbox teaches nobody; syncing again adds no use |
| `scenario=interactive-lane` | `InteractiveLaneTests` | while a Folder sync is paused mid-stream on the sync connection, a body fetch and a `\Flagged` push finish on a second connection and the flag lands on the server; with the lane switched off the same fetch waits until the sync lets go; an idle provider serves a fetch on its one connection |

## When Docker is missing

The tests **skip with a reason**; they never pass quietly and never fake a result. The reason names
what was not verified, for example:

```
Integration tests were SKIPPED: no Docker or Podman endpoint was found (no DOCKER_HOST, no engine
socket). Dovecot and smtp4dev could not be started, so the end-to-end path was NOT verified.
```

A CI leg without Docker must report that line, not a green suite.

## Environment

Credentials come from the environment and are redacted wherever the harness prints anything. When a
variable is unset the harness generates a random per-run value; nothing is hard-coded.

| Variable | Default |
|---|---|
| `MAILCODED_IT_MAILBOX` | `tester@mailcoded.test` |
| `MAILCODED_IT_IMAP_PASSWORD` | random per run |
| `MAILCODED_IT_SMTP_USER` | `smtp-test` |
| `MAILCODED_IT_SMTP_PASSWORD` | random per run |
| `MAILCODED_IT_DOVECOT_IMAGE` | `dovecot/dovecot:2.3.21` |
| `MAILCODED_IT_SMTP4DEV_IMAGE` | `rnwood/smtp4dev:3.6.1` |
| `MAILCODED_SKIP_INTEGRATION` | unset — set it to skip with a reason |
| `MAILCODED_IT_KEEP_WORKSPACE` | unset — set it to keep the temp store for inspection |
| `MAILCODED_DAEMON` | unset — path to the daemon binary for the IDLE test |

The IDLE test drives the shipped `mailcoded-daemon` over stdio. It locates the built binary under
`src/Mailcoded.Daemon/bin/`; build the solution first, or point `MAILCODED_DAEMON` at it. If it
cannot be found the test skips with that reason rather than substituting an in-process stand-in.

Test credentials never reach the local store: the harness uses an in-memory `ISecretStore`. The
daemon subprocess is forced onto the encrypted-file backend, because a CI box has no unlocked
keyring.
