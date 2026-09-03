# Verification record

What has actually been exercised, and what has not. This file exists because
`STRATEGY.md` §5 gates every public claim on a measurement, so "implemented" and
"verified" have to be separable. Nothing here is a performance claim.

Environment: NixOS on WSL2, .NET SDK 10.0.302, linux-x64. Docker was **not** available.

An adversarial review (six independent lenses, then a skeptic per finding instructed to refute it)
produced 16 confirmed defects, all since fixed with regression tests. The store schema is at
`user_version` 5.

## Verified by running it

| Area | What was run | Result |
|---|---|---|
| Build | `dotnet build Mailcoded.slnx` with `TreatWarningsAsErrors=true` | clean, 0 warnings |
| Unit suite | `tests/Mailcoded.Core.Tests` (xunit.v3) | **941 passed, 0 failed** |
| Architecture tests | NetArchTest rules from ARCHITECTURE §12.7 | green, incl. the no-removal-verb rule |
| Native AOT | `dotnet publish -r linux-x64 -p:PublishAot=true`, daemon and CLI | 13.3 MB / 12.0 MB, **zero IL2xxx/IL3xxx warnings** |
| AOT runtime | the AOT **CLI** imported all 35 fixtures and ran FTS, CJK-trigram, short-CJK `LIKE` and metadata search | identical results to the JIT build — MimeKit and SQLitePCLRaw survive trimming |
| Daemon stdio | `scripts/aot-smoke.sh` plus a pipelined 4-request session | `initialize`, `account.list`, `folder.list`, `stats`, `health`, `shutdown` all answered; framing correct |
| FTS5 assertion | startup check against `PRAGMA compile_options` | present; store opens at `user_version` 4 |
| MIME corpus | `mailcoded import-eml fixtures/eml` | 36/36 imported, **0 failures** |
| Search | FTS terms, phrases, diacritics, CJK ≥3 chars (trigram), CJK 1–2 chars (`LIKE`), `from:`, `subject:`, `tag:`, `is:unread`, `is:flagged`, `has:attachment`, `before:`/`after:`, negation | correct hits, correct reported route |
| Malformed query | `search 'from: AND AND "unclosed'` | `ok: true` with a populated `errors` array — never throws |
| IMAP sync | first sync against a minimal local IMAP server | plan `invalidate`, 3 envelopes ingested, 3 batches |
| Re-sync | second sync, same server | `added: 0`, row count unchanged, 96 ms — idempotent |
| Tag↔Flag | `\Seen` / `\Flagged` / unflagged messages | `flags` 0 / 3 / 1; `is:unread` and `is:flagged` return the right rows |
| Lazy body fetch | `read <id>` on an unfetched message | body fetched over IMAP, blob stored, `body_fetched` set on **that row only**, FTS row upgraded — the body became CJK-searchable only after the fetch |
| Offline tagging | `tag <id> +triaged -inbox` with no reachable server | Tag persisted locally and immediately searchable; `push_deferred_reason` reported |
| SMTP send | full two-phase send against a local SMTP sink | `250`, state `sent`, enhanced status recorded |
| **Bcc privacy** | the same send, inspected at the wire | RCPT TO carried **both** recipients; DATA carried **no** `Bcc:` header |
| **Confirm token** | preview in one process, send in another | succeeded once; reuse, cross-draft use and a garbage token all rejected `1003` having sent **no RCPT and no DATA** — exactly one transmission |
| Send gate | agent surface without `MAILCODED_SEND=1`, then without an allowlist match | denied `1006` before any connection was opened or account config read |
| Raw SQL gate | `query --sql` off, then on with a write statement | `1006` when off; "only a single SELECT or WITH" when on |
| No-delete | `delete`, `expunge`, `trash`, `purge`, `remove`, `rm` on the CLI | every one unknown, with a message saying no such verb exists and no flag adds one |
| Agent plaintext | `read` on an HTML-only message with `<script>` and `javascript:` | plaintext only; no script content, no `alert(1)`, no `bodyHtml` key |
| MCP surface | `tools/list` and three `tools/call` round trips | 8 tools, **no** delete/expunge/trash tool, `send_draft` marked dangerous |
| Audit trail | `sync_log` after CLI activity | a row per call with `interface=cli`, a decision, and an args **digest** — no bodies, no credentials |
| Exit codes | one invocation per class | 0 / 2 validation / 3 not-found / 4 forbidden, distinct and documented in `help` |
| Integration suite | run with no Docker | **5 skipped** with the reason printed, 0 failed — never silently passes |
| **Agent send cap** | seven `draft`/`send-preview`/`send-draft` triples, each in a **separate CLI process** | five transmitted, the sixth and seventh refused `1005 rate_limited`; the SMTP sink saw exactly five deliveries and `send_budget` held five rows |
| **HTML text loss** | a message whose body contains `<3`, `<5000`, `<10%` | the payload after each now reaches `body_text` — `wire`, `999-888`, `CFO` and `IMPORTANT` are all searchable |
| Script exclusion | fixture 011 (HTML-only with `<script>` and `javascript:`) | `body_text` carries none of it; the only `alert` match in the corpus is fixture 035's deliberately **undecoded** UTF-7 text |
| Clean clone | `git clone` then build and test | builds and passes — nothing required is missing from git |
| **`setup` wizard** | driven through a pty against a local IMAP server | resolved settings, took a non-echoed password, verified with a real login, saved the account, synced 3 messages; the credential does not appear anywhere in `store.db` |
| `setup` failure path | same, with an unreachable host | reported the host:port that failed with actionable hints and saved **nothing** — no account, no stored credential |
| `setup` guidance | `me@gmail.com` and `me@outlook.com` | Gmail showed the app-password requirement and link *before* prompting; Outlook stopped with the basic-auth explanation instead of failing at login |
| `setup` in a script | piped stdin | refuses with a pointer to `account add --password-stdin`, rather than hanging on a prompt |
| **Install** | `scripts/install.sh` into `~/.local`, then the commands used from PATH with no `--db` | all three commands resolve; `mailcoded import-eml` + `search` work against the default store at `~/.local/share/mailcoded` |
| Install guard | planted a `store.db` where the payload goes, then reinstalled and uninstalled | both refused and the file survived — `$PREFIX/share/mailcoded` **is** the Linux store path, so the payload lives in `libexec` instead |
| Symlink resolution | AOT binary symlinked into a directory with no `libe_sqlite3.so` | works (resolves via `/proc/self/exe`); a bare copy without the library fails, which is why the installer symlinks rather than copies |

## Milestone status against SPEC §9

The SPEC checkboxes are deliberately left unticked: several criteria need Docker, reference
hardware, or the other two OS legs, none of which were available here.

| Milestone | State | What is missing |
|---|---|---|
| **M0** Scaffold | criteria met on linux-x64 | the macOS and Windows CI legs have not run |
| **M1** Store + MIME | met | — |
| **M2** IMAP sync + send | mostly met | verified against a minimal local IMAP server, not Dovecot: the QRESYNC/CONDSTORE arms, IDLE, `APPEND`-to-Sent and the 5k-message timing are unrun |
| **M3** Daemon + secrets + extension skeleton | backend met | the Windows/macOS keyrings are untested, and the VS Code extension does not exist |
| **M4** Read/act/compose in the extension | not started | the extension is a separate repository |
| **M-perf** | harness only | benchmarks never run; `baseline.json` is zeros on purpose |
| **M5** Polish + agents + packaging | agent surface met | `vsce`/Open VSX packaging, screenshots, and the release tag are human steps |
| **M-chaos / M-soak** | not started | no Toxiproxy run, no 24h soak |
| **M6** Launch | drafts only | a human posts; see `docs/launch.md` |

## NOT verified — do not claim these

- **M-perf gates.** The harness and the deterministic 500k corpus generator exist and the generator
  is reproducible (same seed → identical digest), but the benchmarks have **not** been run on
  reference hardware. `baseline.json` is committed with zeros on purpose. Every number in
  PERFORMANCE §15.2 is a target, not a measurement.
- **QRESYNC and CONDSTORE delta paths.** The local test server advertises neither, so only the
  full-diff arm has executed. The planner's delta arms are covered by unit tests, not by a server.
- **IMAP IDLE notifications**, `APPEND`-to-Sent, and `MOVE`.
- **The Windows and macOS keyring backends.** Only `EncryptedFileStore` and the libsecret
  *unavailable* path ran here. The macOS and libsecret interop needs a smoke test on real hardware.
- **The Dovecot / smtp4dev integration path**, including the crash-reconciliation and
  UIDVALIDITY-change tests. Docker was unavailable.
- **M-chaos and M-soak**: no Toxiproxy run, no 24h soak, so the RELIABILITY §14.1 resource budgets
  are unmeasured extrapolations.
- **Windows and macOS CI legs.** The matrix is defined in `.github/workflows/ci.yml` and has not run.
- **The VS Code extension.** Not built.

## Reproducing the local checks

```bash
scripts/build.sh                                   # restores offline if nuget.org is unreachable
scripts/test.sh                                    # the 869-test unit suite
dotnet publish src/Mailcoded.Daemon -c Release -r linux-x64 -p:PublishAot=true -o artifacts/linux-x64
scripts/aot-smoke.sh artifacts/linux-x64
scripts/size-gate.sh artifacts/linux-x64
dotnet run -c Release --project tests/Mailcoded.Bench -- --generate --size 10000 --verify
```
