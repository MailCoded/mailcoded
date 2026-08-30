# CLAUDE.md — mailcoded

Read `SPEC.md` before doing anything. Work milestone-by-milestone (SPEC §9); never start a milestone before the previous one's acceptance criteria pass. When SPEC and this file conflict, **this file wins** — and say so in your summary.

Design docs in `docs/` are normative where SPEC is silent: `ARCHITECTURE.md`, `AGENT-INTERFACE.md`, `RELIABILITY.md`, `PERFORMANCE.md`, `DEPENDENCIES.md`.

## What this is
Local-first email: a cross-platform .NET daemon (`mailcoded`) syncing IMAP (Graph in v0.2) into SQLite+FTS5, speaking JSON-RPC over stdio; a VS Code extension client; a gated agent surface. Org: github.com/Mailcoded.

## Commands

```bash
dotnet build Mailcoded.slnx
dotnet test                                    # unit + daemon golden-file tests
dotnet test tests/Mailcoded.Integration        # needs Docker (Dovecot, smtp4dev)
dotnet run --project src/Mailcoded.Daemon      # stdio, Content-Length framing
dotnet run --project src/Mailcoded.Cli -- search "tag:unread"
dotnet publish src/Mailcoded.Daemon -c Release -r linux-x64 /p:PublishAot=true
dotnet run -c Release --project tests/Mailcoded.Bench   # M-perf gates
```

On NixOS/WSL: `nix develop` first; `programs.nix-ld` must be enabled on the host for the AOT smoke test. If Docker is unavailable, run unit tests only and **say integration tests were skipped** — never fake their results.

## Hard invariants — never violate, never "temporarily" disable

1. **AOT stays green.** System.Text.Json source generators only; no `Reflection.Emit`; no new NuGet package unless the AOT CI job passes with it. Floors: MailKit ≥ 4.17.0, MimeKit ≥ 4.15.1, `SQLitePCLRaw.bundle_e_sqlite3`.
2. **No S/MIME / PGP / BouncyCastle anywhere.** Out of scope and AOT-hostile.
3. **Secrets never touch disk, DB, config JSON, logs, RPC responses, or exception messages.** Only `ISecretStore`. Test credentials come from env vars and are redacted in output.
4. **Email content is untrusted attacker input.** Never pass bodies/subjects/headers into shell commands, terminals, `eval`, prompts, file paths, or SQL strings (parameterized queries only). In the extension, HTML renders only inside the sandboxed webview after DOMPurify, under the strict CSP in SPEC §6.3 — **no remote origins in CSP, ever**. Sanitization/CSP changes are security-sensitive: add tests and flag for human review.
5. **Send requires a valid one-time token** at the RPC layer. The agent surface additionally requires `MAILCODED_SEND=1` + recipient allowlist + ≤5/hr. **No delete/expunge/trash tool may exist in the CLI or MCP surface — absent, not gated.** Every send and tag mutation writes to `sync_log`.
6. **The extension never opens network connections to mail servers** and never uses VS Code file watchers on mail data. The mail store never lives inside a workspace folder.
7. **Windows correctness:** never write `:`-separated Maildir filenames on Windows paths (`!` on export); store root stays `%LOCALAPPDATA%\mailcoded`; compare folder names case-insensitively on Windows.
8. **Wrap MailKit/MimeKit behind Core interfaces.** `MimeMessage` and raw RFC822 bytes never escape `Providers`/`Parsing`. Hosts (Daemon/Cli/Mcp) contain no business logic.
9. **Server wins on flags; local wins on custom tags.** Sync batches are single transactions, idempotent on re-run.
10. **Don't relitigate `[DECIDED]` items.** Implement to spec and raise concerns in the summary — or stop and ask if blocking.
11. **Dependency direction:** `Domain` references nothing beyond the BCL; `Application` never touches MailKit/MimeKit/Sqlite types; adapters never reference each other; only host composition roots construct concrete adapters. The architecture tests encoding this are required checks — never weaken them to make a change compile.
12. **Anti-ceremony — do not introduce:** MediatR or any mediator/CQRS bus (RPL/commercial since 2025; reflection discovery is AOT-hostile; ~17 RPC methods need a `switch`). AutoMapper/Mapster — hand-write DTO mapping (Mapperly, Apache-2.0 source-gen, is the only pre-approved fallback). Repository interfaces over `SqliteStore`. Generic `Repository<T>` / `UnitOfWork` / entity base classes. `Result<T>` libraries — use the SPEC §12.5 exception policy. Generic Host / `BackgroundService` layers. **EF Core** (AOT-experimental + size). No new interface without a process/IO boundary or a second real implementation; approved ports are `IMailProvider`, `ISecretStore`, `IThreader`, `IClock`, and the stdio transport.
13. **Purity of the sync core:** `Domain/Sync`, `Domain/Tags`, `Domain/Threading` stay pure — no I/O, no ambient clocks (inject `IClock` values as parameters), no logging. New sync edge cases extend `SyncPlanner`/`Apply` plus a table-driven test — never a new branch inside `SyncEngine`.
14. **Vocabulary is normative:** Account, Folder, Envelope, Message, Blob, **Tag** (local) vs **Flag** (server IMAP), Plan, SyncEvent, Outbox, Quirk. No synonyms ("label" for Tag, "mailbox" for Folder) in code or docs.
15. **Performance discipline:** SQLite calls are **synchronous** on the writer thread (async is fake for SQLite and only adds allocation). Ordinal reads only — no `GetOrdinal`-by-name in hot loops. **No OFFSET pagination beyond ~page 20** — keyset seek is mandatory. Prepared-command reuse is mandatory in hot paths. FTS writes are deferred during backfill. M-perf gates must pass before merging any `Store` change.
16. **Monotonic time for intervals.** All timers/backoff use `Stopwatch`/`Environment.TickCount64`; wall clock is display-only. Any IDLE/command timeout is a reconnect trigger; detect wall-clock jumps to catch suspend/resume.

## Conventions
- C#: file-scoped namespaces, nullable enabled, `TreatWarningsAsErrors=true`; async all the way with `CancellationToken` on every public async API; no `async void`.
- One migration per schema change, forward-only, bump `PRAGMA user_version`; never edit an applied migration.
- RPC DTOs are the source of truth; regenerate TS types (`npm run gen:types`) in the same PR as any DTO change.
- Errors: stable numeric RPC codes (SPEC §5.6); add to `docs/rpc.md` when introducing one.
- Commits: conventional (`feat:`, `fix:`, `test:`, `perf:`, `chore:`); one milestone task per PR where practical.

## Definition of done (every PR)
- Builds + tests green on all three OS legs; AOT job green; architecture tests green.
- New behavior has tests (unit minimum; golden-file for RPC changes; a fixture for any MIME edge case encountered).
- Domain logic added this PR is unit-tested **without SQLite, sockets, or mocks**.
- No invariant weakened; security-sensitive changes flagged.
- The SPEC checkbox(es) it satisfies named in the PR description.

## Fixtures & test servers
`fixtures/eml/` is **append-only**: when a real message breaks parsing, add a minimized, anonymized fixture reproducing it *before* fixing. Integration stack: Dovecot (IMAP) + smtp4dev (SMTP sink) via Testcontainers; helpers in `tests/Mailcoded.Integration/Support`.

## Things Claude Code must NOT do
- Publish, tag releases, or post to Marketplace / Open VSX / HN / Reddit — drafts only; a human publishes.
- Send real email or add real credentials; use containers and env-var test creds.
- Add telemetry, analytics, or network calls beyond mail protocols and (v0.2) Graph/MSAL endpoints.
- Delete or rewrite `sync_log`, migrations, or fixtures.
- Add an in-app auto-updater (also blocked by Obsidian store policy for the shared codebase).
