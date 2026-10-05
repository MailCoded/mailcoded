# Verification record

What has actually been exercised, and what has not. This file exists because
`STRATEGY.md` §5 gates every public claim on a measurement, so "implemented" and
"verified" have to be separable. Nothing here is a performance claim; the one set of timings recorded
below is a development-machine measurement, labelled as such, and is not a reference-hardware result.

Environment: NixOS on WSL2, .NET SDK 10.0.302, linux-x64. Docker was **not** available for the
earlier rows; an earlier run had it, and the original five Dovecot/smtp4dev tests ran then (see below).

An adversarial review (six independent lenses, then a skeptic per finding instructed to refute it)
produced 16 confirmed defects, all since fixed with regression tests. A second review, of the work that
added the SPEC §9 M2a features (correctness, security, reliability and performance, protocol and docs,
tests), found further defects; each was fixed with a regression test, or declined with a recorded
measurement (the `tag:` subquery rewrite, and two indexes), or refuted.

**What was run for the M2a features, and on which tree.** The features were built one at a time on a
growing tree, and each was checked when it landed, so a row below says which tree it ran on.

- On the final tree (after the review's fixes and the documentation pass): the build (0 warnings,
  0 errors), the whole unit suite (counts in the table), the TypeScript protocol check (`--check`, up
  to date), and `mailcoded import-eml fixtures/eml` on a fresh store (56 files, 56 imported, 0 failures).
  The store schema is at `user_version` 17 (migrations 009 to 017 were added since the oldest rows).
- On the tree at the time each feature landed, not repeated on the final tree: a Native AOT publish of
  the daemon (zero IL2xxx/IL3xxx warnings after each of the signals, unsubscribe, backup, sync progress,
  identities, server drafts, correspondents and attachment text work; the review's fixes and the
  documentation pass were **not** published), the golden transcript diff, and the AOT binaries driven
  over stdio or from the shell (rows below).
- Every other row below ran on an earlier tree, at `user_version` 8, with 37 fixtures, and says nothing
  about code written since. No Docker, no pty run and no Windows or macOS leg happened in any of it.

## Verified by running it

| Area | What was run | Result |
|---|---|---|
| Build | `dotnet build Mailcoded.slnx` with `TreatWarningsAsErrors=true` | clean, 0 warnings |
| Unit suite | `tests/Mailcoded.Core.Tests` (xunit.v3), final tree | **3524 passed, 0 failed, 1 skipped (the env-gated 100k performance test)** |
| Architecture tests | NetArchTest rules from ARCHITECTURE §12.7 | green, incl. the no-removal-verb rule |
| Native AOT | `dotnet publish -r linux-x64 -p:PublishAot=true` | `mailcoded-daemon` 16.25 MiB and `mailcoded` 15.53 MiB, **zero IL2xxx/IL3xxx warnings**; both under the 20 MB fail gate, both over the 12 MB warning |
| Native AOT, M2a features | `dotnet publish -r linux-x64 -p:PublishAot=true` of the daemon, once per feature, on the tree at that moment (and the CLI and TUI for some) | zero IL2xxx/IL3xxx or other warnings every time; the last publish was before the review's fixes, so the final tree is **not** covered. Sizes are not recorded here: DEPENDENCIES.md has the current figures |
| AOT binaries driven, M2a features | the AOT CLI: `account add --trust-authserv-id`, `account trust` (shown, refused without the unlock, changed with it, `--none`), then `import-eml` of fixture 046 and `search is:verified` (0 hits) and `-is:verified` (1 hit). The AOT daemon over stdio: `initialize` advertising `capabilities.backup` and `notify.sync.progress`, `store.backup` into a new folder then `store.backupVerify` (ok), `sync` with `progress: true` on an unknown account (error, no progress), `correspondent.suggest` on a fresh store (1002 for an unknown account) | as stated; the backup folder held `manifest.json` and `store.db`, both mode 0600. Each ran on the tree of its own feature |
| Protocol types and golden transcripts | TypeScript generator with `--check` after every DTO change; golden request/response pairs for each new RPC behaviour, regenerated and the diff read | up to date; every golden change was a field or method the feature added, or a schema version |
| Query-plan probes | throwaway scripts, not `tests/Mailcoded.Bench` and not reference hardware: Trash/Junk exclusion on a synthetic 500k-row store (plan unchanged; about 20-25 ms more for a date-ordered FTS term matching 100k rows); `attachment:` over 20,000 attachments (7-32 ms); `is:encrypted` over 300k rows (82 ms before the partial index, 0.02 ms after) | recorded in PERFORMANCE.md; these are measurements of a plan, not benchmark results |
| Size gate | `scripts/size-gate.sh` over the publish directory | every file counted, incl. `libe_sqlite3.so` (1.40 MiB); a 29 MB decoy library fails the gate |
| Encoder AOT cost | byte count of the published daemon with and without the encoder reachable | **+158,000 bytes** (0.9%); 3.5x the design's 45,144 B estimate |
| AOT runtime | the AOT **CLI** (earlier tree) imported all 37 fixtures and ran FTS, CJK-trigram, short-CJK `LIKE` and metadata search | identical results to the JIT build — MimeKit and SQLitePCLRaw survive trimming |
| Daemon stdio | `scripts/aot-smoke.sh` plus a pipelined 4-request session | `initialize`, `account.list`, `folder.list`, `stats`, `health`, `shutdown` all answered; framing correct |
| FTS5 assertion | startup check against `PRAGMA compile_options` | present; a fresh store opened at `user_version` 8 then (now 17, asserted by unit tests) |
| MIME corpus | `mailcoded import-eml fixtures/eml`, final tree, JIT CLI | 56/56 imported, **0 failures** (37/37 on the earlier tree) |
| Migration 008 | fresh store, and store at v7 (earlier tree) | reaches `user_version` 8; `msg_vec`, `vec_model` present; account forget cascades to `msg_vec` |
| Semantic pipeline | synthetic random-weight model in `<data>/model`; daemon in stdio mode; then CLI | daemon logged the model and `capabilities.semantic: true`; backfill wrote 37/37 vectors in the background; `search 'invoice' --meaning` returned `semantic: true` with the lexical hit still ranked first; `vec_model` row count unchanged after the CLI (it attaches read-only) |
| Semantic refusal | `search` with `semantic: true` and no model | **1008** with category `unsupported` (golden transcript) |
| Backfill vs bulk ingest | unit test opening a bulk-ingest window | the worker stands down and writes nothing inside the window; a write issued inside it would join that window's transaction and be rolled back with it (found by test, fixed) |
| Dovecot / smtp4dev | the original five tests in `tests/Mailcoded.Integration` via Testcontainers, on an earlier tree | **5 passed, 0 skipped**: add-account/sync/search/tag/send round trip; crash after SMTP 250 reconciles from Sent; stuck send is investigated, never resent; UIDVALIDITY change re-enumerates without duplicating blobs; external APPEND produces `notify.mail.added` within 5 s |
| Search | FTS terms, phrases, diacritics, CJK ≥3 chars (trigram), CJK 1–2 chars (`LIKE`), `from:`, `subject:`, `tag:`, `is:unread`, `is:flagged`, `has:attachment`, `before:`/`after:`, negation (earlier tree) | correct hits, correct reported route |
| Search, newer operators | `attachment:` (text-like attachments and `.docx`), `in:trash`/`in:anywhere`, Trash and Junk left out by default, `is:automated`, `is:verified`, `is:encrypted` | **unit tests only**; no Docker or daemon session exercised them |
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
| Integration suite | run with no Docker (earlier tree, when it held five tests) | **5 skipped** with the reason printed, 0 failed — never silently passes |
| **Agent send cap** | seven `draft`/`send-preview`/`send-draft` triples, each in a **separate CLI process** | five transmitted, the sixth and seventh refused `1005 rate_limited`; the SMTP sink saw exactly five deliveries and `send_budget` held five rows |
| **HTML text loss** | a message whose body contains `<3`, `<5000`, `<10%` | the payload after each now reaches `body_text` — `wire`, `999-888`, `CFO` and `IMPORTANT` are all searchable |
| Script exclusion | fixture 011 (HTML-only with `<script>` and `javascript:`) | `body_text` carries none of it; the only `alert` match in the corpus is fixture 035's deliberately **undecoded** UTF-7 text |
| Clean clone | `git clone` then build and test | builds and passes — nothing required is missing from git |
| Account admin verbs | `account list`, `account test` against a store with no reachable server | list reports folders/messages/unread/last-sync; test separates a network failure from a dead credential and exits with the matching code |
| `attachments` | fixture 014 (PDF) and 015 (path-traversal filename) | listed with type and size; `--save` wrote the file, and `../../../../etc/cron.d/pwn` was sanitized to `pwn` |
| `reply` | replying to fixture 001 | correct `Re:` subject, recipient from the original, and **In-Reply-To and References both pointing at the original Message-ID** with the body quoted |
| `move` gating | `--yes` without the unlock, then with it | denied before any folder name is revealed; with `MAILCODED_ALLOW_MOVE=1` it proceeds to resolution |
| **`setup` wizard** | driven through a pty against a local IMAP server | resolved settings, took a non-echoed password, verified with a real login, saved the account, synced 3 messages; the credential does not appear anywhere in `store.db` |
| `setup` failure path | same, with an unreachable host | reported the host:port that failed with actionable hints and saved **nothing** — no account, no stored credential |
| `setup` guidance | `me@gmail.com` and `me@outlook.com` | Gmail showed the app-password requirement and link *before* prompting; Outlook stopped with the basic-auth explanation instead of failing at login |
| `setup` in a script | piped stdin | refuses with a pointer to `account add --password-stdin`, rather than hanging on a prompt |
| **Install** | `scripts/install.sh` into `~/.local`, then the commands used from PATH with no `--db` | all four commands resolve; `mailcoded import-eml` + `search` work against the default store at `~/.local/share/mailcoded` |
| Install guard | planted a `store.db` where the payload goes, then reinstalled and uninstalled | both refused and the file survived — `$PREFIX/share/mailcoded` **is** the Linux store path, so the payload lives in `libexec` instead |
| Symlink resolution | AOT binary symlinked into a directory with no `libe_sqlite3.so` | works (resolves via `/proc/self/exe`); a bare copy without the library fails, which is why the installer symlinks rather than copies |

## Development-machine measurement: 100,000 Messages

Not reference hardware, not the M-perf baseline, and not a launch claim. It was run once, to find out
whether the read paths hold up at 100k and to give the opt-in test (`MAILCODED_TEST_PERF=1`) a
starting point for its ceilings.

- **Machine:** Intel Core Ultra 7 165H, 20 logical CPUs, 25 GB RAM, WSL2 on NixOS 26.05, .NET SDK
  10.0.302 (runtime 10.0.10), linux-x64. Storage was not recorded.
- **What ran:** `LargeCorpusPerformanceTests`, Release build, one run, idle machine. It seeds the
  `tests/Mailcoded.Bench` corpus generator (seed 20260101, fingerprint `081ccbce2582`, about 10%
  CJK) through the bulk ingest path, then times each read path from a warm process. The seed took
  about 122 s and the database came to 676 MiB; body text is written one transaction per Message
  after the bulk window closes, and that is most of the 122 s.
- **Largest Folder:** INBOX, 55% of the corpus (about 55,000 Messages); the thread view scans that Folder, not all 100,000 rows.

| Path | n | p50 | p95 | Target at 500k | Target scaled to 100k | Test ceiling |
|---|---|---|---|---|---|---|
| Envelope page, first (50 rows) | 200 | 0.055 ms | 0.115 ms | < 10 ms | 10 ms | 100 ms |
| Envelope page, deep (50 rows, past row 27,561) | 200 | 0.060 ms | 0.097 ms | < 10 ms | 10 ms | 100 ms |
| FTS search (five queries, incl. phrase and CJK) | 100 | 50.7 ms | 109.9 ms | < 100 ms p95 | 20 ms | 600 ms |
| Unread badge (all Folders) | 500 | 0.018 ms | 0.021 ms | < 5 ms | 5 ms | 50 ms |
| Thread view, page of 50 threads | 100 | 96.1 ms | 105.7 ms | < 20 ms | 4 ms | 240 ms |
| Thread view, one thread's Messages | 200 | 0.035 ms | 0.051 ms | < 20 ms | 20 ms | 200 ms |

A Debug build gave the same picture (FTS p95 114 ms, thread page p95 107 ms): SQLite dominates.

**Two paths are above their scaled targets, and their test ceilings are widened to match.** The thread
page's `ROW_NUMBER()` partition has no index to walk, so it sorts every Message in the Folder on every
call; at 500k that is a cost to expect well past the 20 ms target. FTS over a common term ranks every
match before taking 50, so its cost grows with the match count. The ceilings (6x and 3x the
scaled-target ceiling) exist to catch a regression from today's behaviour, not to excuse it. Treat both
as open performance work. Seek-shaped paths (envelope pages, the unread counter, one thread's
Messages) are far inside their targets and keep the plain 10x ceiling.

The 500k BenchmarkDotNet suite, the 15% regression gate and any reference-hardware number remain
unrun.

## Milestone status against SPEC §9

The checkboxes for M0 to M-chaos are deliberately left unticked: several criteria need Docker, reference
hardware, or the other two OS legs, none of which were available here. The M2a block ticks only the
criteria whose check is named in the rows above, and says why each unticked one is unticked.

| Milestone | State | What is missing |
|---|---|---|
| **M0** Scaffold | criteria met on linux-x64 | the macOS and Windows CI legs have not run |
| **M1** Store + MIME | met | — |
| **M2** IMAP sync + send | mostly met | Dovecot and smtp4dev ran for the original 5 tests: the QRESYNC/CONDSTORE arms and the 5k-message timing are still unrun |
| **M2a** Owner-added feature set | met at unit level | every Dovecot and integration check: 15 test methods in nine files were written and never run; nothing exercised signals, hierarchy, progress, identities, server drafts, `unsubscribe`, attachment text or the interactive IMAP connection against a real server; the final tree has no AOT publish or size-gate result recorded here. The native Gmail REST provider was not built |
| **M3** Daemon + secrets + extension skeleton | backend met | the Windows/macOS keyrings are untested; the VS Code extension is a separate repository this record does not cover |
| **M4** Read/act/compose in the extension | partly, elsewhere | reading, search and the sandboxed reader exist in the extension's own repository; compose and send do not. Nothing here tests it |
| **M-perf** | harness only | the BenchmarkDotNet suite has never run; `baseline.json` is zeros on purpose. A 100k-message read-path test has run once on a development machine (below), which is not reference hardware |
| **M5** Polish + agents + packaging | agent surface met | `vsce`/Open VSX packaging, screenshots, and the release tag are human steps |
| **M-chaos / M-soak** | not started | no Toxiproxy run, no 24h soak |
| **M6** Launch | drafts only | a human posts; see `docs/launch.md` |

## NOT verified — do not claim these

- **M-perf gates.** The harness and the deterministic 500k corpus generator exist and the generator
  is reproducible (same seed → identical digest), but the benchmarks have **not** been run on
  reference hardware. `baseline.json` is committed with zeros on purpose. Every number in
  PERFORMANCE §15.2 is a target, not a measurement. The 100k development-machine run above is the
  only timing on record, and two of its paths are above target.
- **The newer integration tests.** `tests/Mailcoded.Integration` holds 15 more test methods in eight
  files (message signals, sync progress, sender identities, server drafts, correspondents,
  List-Unsubscribe, the interactive IMAP lane, attachment text). They are written but have never run: no Docker was
  available, so they prove nothing yet, and the MailKit-facing code they cover (folder hierarchy, the interactive lane,
  `APPENDUID` handling, server-draft `UID MOVE`, envelope signals) is checked by unit tests against fakes only.
- **Anything the M2a features do against a real server.** Header acquisition (`HEADER.FIELDS` beside the
  envelope items, so `automated` and `dmarc` on synced mail), the folder tree on a server whose
  delimiter is not `/`, the numbers `notify.sync.progress` reports during a real sync, `APPENDUID`
  handling, the server draft's `UID MOVE` into Sent, and the interactive IMAP connection. `ImapProvider`
  routing and `MoveAtomicAsync` have no unit seam, so only the unrun Docker tests would cover them.
- **`is:verified` on any provider but Gmail.** Only `imap.gmail.com` ships a trusted authserv-id
  (`mx.google.com`), taken from knowledge of that provider and not checked against a live message. Nothing
  was run against Microsoft, Yahoo, Fastmail or iCloud mail.
- **The TUI additions.** The sync status line, the folder tree in the sidebar, recipient completion
  (`ctrl-n`) and the Trash and Junk search pinning are covered by unit tests of their pure parts. None
  was driven in a terminal, and no interactive run of the CLI's progress line happened either.
- **Backup on Windows and macOS, and at scale.** The destination rules for Windows device names and
  alternate data streams are unit-tested as strings only. A backup of a large live store under
  sustained writes was not run (the copy holds one read transaction while it runs).
- **A human review of the untrusted-input parsers.** `.docx` extraction (`WordXmlTextScanner`,
  `DocxTextExtractor`, `AttachmentTextExtractor`) and `Authentication-Results` parsing have adversarial
  unit tests, not an outside review or fuzzing run.
- **The final tree under Native AOT.** See the AOT rows: the last publish predates the review's fixes.
- **A native Gmail REST provider.** Not built; the network rule forbids the HTTPS calls it would need.
- **QRESYNC and CONDSTORE delta paths.** The local test server advertises neither, so only the
  full-diff arm has executed. The planner's delta arms are covered by unit tests, not by a server.
- **`MOVE`**, and the 5k-message sync timing.
- **The published `mailcoded-mcp` artifact.** Its tools are unit-tested, but CI only compiles the
  adapter — nothing starts the published binary, so a failure that survives compilation would ship.
- **The Windows and macOS keyring backends.** Only `EncryptedFileStore` and the libsecret
  *unavailable* path ran here. The macOS and libsecret interop needs a smoke test on real hardware.
- **Search-by-meaning quality.** The pipeline runs end to end (above), but only against a model
  with **random weights**, which proves plumbing and nothing about relevance. No model is pinned:
  `scripts/install.sh` ships with `MODEL_URL` and `MODEL_SHA256` empty and refuses to download. No
  recall@10 harness exists. **It has never been shown to beat lexical search.** Do not claim it does.
- **Semantic search p95 at 500k.** The `< 100 ms` figure in PERFORMANCE §15.2 is a target. The
  "11.8 ms" scan number quoted there came from an exploratory probe on the development machine, not
  from `tests/Mailcoded.Bench`, which has no semantic gate yet.
- **A similarity floor.** None exists, so a semantic search fills the page to `limit`. Choosing a
  threshold needs a real model to calibrate against.
- **Memory with a real model.** Weights are held as managed `float[]`; a MiniLM-class model is
  roughly 90 MB on the GC heap against the daemon's `HeapHardLimitPercent=25`. Only the 32 KB
  synthetic model has run.
- **M-chaos and M-soak**: no Toxiproxy run, no 24h soak, so the RELIABILITY §14.1 resource budgets
  are unmeasured extrapolations.
- **Windows and macOS CI legs.** The matrix is defined in `.github/workflows/ci.yml` and has not run.
- **The VS Code extension.** A separate repository. It has its own unit suite and is unreleased;
  nothing in this record covers it, and no run in this repository has exercised it.

## Reproducing the local checks

```bash
scripts/build.sh                                   # restores offline if nuget.org is unreachable
scripts/test.sh                                    # the unit suite; the table above records the count
dotnet publish src/Mailcoded.Daemon -c Release -r linux-x64 -p:PublishAot=true -o artifacts/linux-x64
scripts/aot-smoke.sh artifacts/linux-x64
scripts/size-gate.sh artifacts/linux-x64
dotnet run -c Release --project tests/Mailcoded.Bench -- --generate --size 10000 --verify
```
