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
