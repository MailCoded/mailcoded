# mailcoded — Project Specification

**Version:** 1.1
**Owner:** Andrew (@lywedo) · Org: https://github.com/Mailcoded
**Audience:** Claude Code (spec-driven execution) + human review
**Status:** Approved for build

---

## 0. How to use this document

This is the executable specification. It is the source of truth for scope, architecture, and acceptance criteria. Work milestone-by-milestone (§9); do not start a milestone until the previous one's acceptance criteria pass. `CLAUDE.md` holds build commands and hard invariants — read it at the start of every session. When this spec and CLAUDE.md conflict, CLAUDE.md's invariants win; flag the conflict.

**[DECIDED]** = settled, do not relitigate. **[OPEN]** = needs a human decision.

Companion design docs in `docs/`: `ARCHITECTURE.md` (§12), `AGENT-INTERFACE.md` (§13), `RELIABILITY.md` (§14), `PERFORMANCE.md` (§15), `DEPENDENCIES.md`. They are normative where this file is silent.

---

## 1. Vision and positioning

**mailcoded** is a local-first email system for people who live in their editor:

1. **`mailcoded`** — a cross-platform (Windows/macOS/Linux) mail backend daemon in C#/.NET 10. Syncs IMAP (Microsoft Graph in v0.2) into a local SQLite store with FTS5 full-text search and notmuch-style tags; sends via SMTP; exposes everything over JSON-RPC 2.0 on stdio. Native AOT single-file binaries. **"One backend, many clients."**
2. **`mailcoded`** (VS Code extension) — a full mail client: sidebar, threaded list, reader, compose. Plaintext-first, tracking-pixel-blocking, no telemetry.
3. **`mailcoded-mcp`** — a thin MCP adapter over the same core so agents can triage and draft under strict safety gates.

**Positioning:** headline *"Local-first email in your editor — your mail, full-text search, no tracking."* Subtitle *"The mu4e/aerc experience for people who live in VS Code."* AI/MCP is the second paragraph, never the headline.

**Why this exists (verified 2026):** no VS Code/Open VSX extension browses a local mail store; the classic Unix stack (mbsync/notmuch/msmtp) is dead on native Windows (no maintained notmuch; Maildir's `:` separator is an illegal NTFS char; oama is POSIX-only); EWS is being disabled from Oct 1 2026 so Microsoft Graph is the durable work-mail path — and none of the Unix stack speaks Graph. A .NET backend on MailKit + Microsoft.Graph covers all three gaps.

---

## 2. Repositories

| Repo | Contents | License | Artifact |
|---|---|---|---|
| `Mailcoded/mailcoded` | `Mailcoded.Core`, `Mailcoded.Daemon`, `Mailcoded.Cli`, `Mailcoded.Mcp`, tests, fixtures | MIT | AOT binaries per RID (GitHub Releases) |
| `Mailcoded/mailcoded-vscode` | VS Code extension (TypeScript, esbuild) | MIT | `.vsix` on Marketplace **and** Open VSX |

Backend first (M0–M2); the extension consumes released daemon binaries from M3. Bundle the platform-matching daemon binary in each platform-specific `.vsix`, or download-on-first-run with SHA-256 verification. `@mailcoded/protocol` (TS types generated from the C# DTOs) publishes from the backend repo — no separate repo.

---

## 3. Scope

### 3.1 In scope — v0.1
- One IMAP account (app password or XOAUTH2), sync to local SQLite store.
- Incremental sync: UIDVALIDITY tracking, QRESYNC/CONDSTORE fast resync, full-fetch fallback, lazy body fetch.
- FTS5 search, notmuch-compatible tags (`unread`, `flagged`, `replied`, custom).
- Send via SMTP with mandatory confirmation + outbox state machine.
- JSON-RPC daemon (stdio) + thin one-shot CLI (`mailcoded`, JSON output).
- Secrets in OS keyring with encrypted-file fallback.
- VS Code extension: sidebar, threaded list, reader (plaintext-first, sanitized HTML toggle, remote images blocked), tag/archive, compose/reply/send.
- MCP adapter: read/search/draft; send gated; **no delete tools exist**.
- SKILL.md for shell-capable agents.

### 3.2 In scope — v0.2
- **GraphProvider**: MSAL.NET device-code flow, delta queries, `Mail.Read`/`Mail.Send`. Flagship differentiator.
- Gmail OAuth2, multi-account.
- **Localhost HTTP/WS + Unix-socket/named-pipe listener** (`mailcoded --listen`) with full auth hardening — the keystone that unlocks every other client. See `PRODUCT-FAMILY.md`.
- Maildir export/import (POSIX `:`, Windows `!`), optional notmuch interop on POSIX.

### 3.3 Explicitly out of scope (v0.x)
POP3, JMAP (v0.3+), calendars/contacts, PGP, S/MIME (also excluded from the AOT path), HTML compose, attachment upload in composer, mobile/web clients, IMAP server mode, message-rules engine, attachment-content indexing (disclose this gap in the README as roadmap).

**The extension never opens network connections to mail servers.** Only the daemon does.

---

## 4. Architecture overview

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

Key properties: transport-agnostic core; SQLite is the single source of truth on all platforms (Maildir is import/export only); LSP-style daemon so connection state (IDLE, auth) amortizes across calls; **all safety gates live in Core, never in an adapter**.

---

## 5. Backend specification

### 5.1 Tech stack — pinned **[DECIDED]**

| Concern | Choice | Constraint |
|---|---|---|
| Runtime | .NET 10, `net10.0` | `PublishAot=true` for Daemon/Cli |
| IMAP/SMTP | **MailKit ≥ 4.17.0** | |
| MIME | **MimeKit ≥ 4.15.1** (security floor) | Prefer `MimeKitLite` in the AOT daemon |
| SQLite | `Microsoft.Data.Sqlite` + **`SQLitePCLRaw.bundle_e_sqlite3`** | Assert `ENABLE_FTS5` at startup and in tests |
| JSON | `System.Text.Json` **source generators only** | No reflection serialization anywhere |
| OAuth (v0.2) | `Microsoft.Identity.Client` + `Microsoft.Graph` | Device-code default |
| Secrets | git-credential-manager store pattern | Never in DB/config/logs |
| Testing | xUnit; Testcontainers (Dovecot + smtp4dev) | |
| CI | GH Actions matrix per RID on its native OS | AOT publish + smoke test on every PR |

Full dependency vetting rule and approved allowlist: `docs/DEPENDENCIES.md`.

### 5.2 Solution layout

```
mailcoded/
├── Mailcoded.sln
├── src/
│   ├── Mailcoded.Core/          # see ARCHITECTURE.md §12.1 for internal folders
│   ├── Mailcoded.Daemon/        # stdio JSON-RPC host (+ one-shot CLI mode)
│   ├── Mailcoded.Cli/           # thin arg parser -> Daemon one-shot
│   └── Mailcoded.Mcp/           # MCP adapter (M5)
├── tests/
│   ├── Mailcoded.Core.Tests/
│   ├── Mailcoded.Integration/   # Testcontainers
│   └── Mailcoded.Bench/         # BenchmarkDotNet (M-perf)
├── fixtures/eml/                # append-only corpus
├── docs/                        # ARCHITECTURE, AGENT-INTERFACE, RELIABILITY, PERFORMANCE, DEPENDENCIES
├── .github/workflows/ci.yml
├── CLAUDE.md
├── SPEC.md
├── ROADMAP.md
└── flake.nix                    # devShell: dotnet-sdk_10, sqlite, docker client
```

### 5.3 Storage schema **[DECIDED]**

DB at `%LOCALAPPDATA%\mailcoded\store.db` (Windows — short root for MAX_PATH), `~/Library/Application Support/mailcoded/store.db` (macOS), `$XDG_DATA_HOME/mailcoded/store.db` (Linux).

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE accounts (
  id INTEGER PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  display_name TEXT,
  provider TEXT NOT NULL CHECK (provider IN ('imap','graph','jmap','gmail')),
  config_json TEXT NOT NULL          -- hosts/ports/auth kind; NEVER secrets
);

CREATE TABLE folders (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  role TEXT,                         -- inbox|sent|drafts|trash|archive|junk|NULL
  uidvalidity INTEGER,
  uidnext INTEGER,
  highestmodseq INTEGER,
  delta_token TEXT,                  -- Graph deltaLink / JMAP state (v0.2+)
  unread_count INTEGER NOT NULL DEFAULT 0,   -- denormalized; see PERFORMANCE.md
  total_count INTEGER NOT NULL DEFAULT 0,
  UNIQUE (account_id, name)
);

CREATE TABLE messages (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
  uid INTEGER,
  message_id TEXT,
  thread_key TEXT,
  date_utc INTEGER,
  from_addr TEXT, to_addrs TEXT, cc_addrs TEXT,
  subject TEXT,
  flags INTEGER NOT NULL DEFAULT 0,  -- bitfield; bit0 = unread
  modseq INTEGER,
  size INTEGER,
  has_attachments INTEGER NOT NULL DEFAULT 0,
  blob_id INTEGER REFERENCES blobs(id),
  body_fetched INTEGER NOT NULL DEFAULT 0,
  UNIQUE (folder_id, uid)
);

CREATE TABLE blobs (
  id INTEGER PRIMARY KEY,
  sha256 TEXT NOT NULL UNIQUE,
  bytes BLOB,                         -- inline if <= 512KB
  ext_path TEXT                       -- content-addressed file if > 512KB
);

CREATE TABLE tags (
  message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
  tag TEXT NOT NULL,
  PRIMARY KEY (message_id, tag)
);

CREATE TABLE body_text (
  message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
  text TEXT NOT NULL                  -- plaintext extraction
);

CREATE TABLE outbox (
  id INTEGER PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id),
  message_id TEXT NOT NULL,           -- pre-assigned at creation, idempotency key
  state TEXT NOT NULL CHECK (state IN ('queued','sending','sent','failed')),
  raw BLOB NOT NULL,
  smtp_response TEXT,
  attempts INTEGER NOT NULL DEFAULT 0,
  next_attempt_utc INTEGER,
  created_utc INTEGER NOT NULL
);

CREATE TABLE sync_log (
  id INTEGER PRIMARY KEY,
  ts INTEGER NOT NULL,
  account_id INTEGER,
  level TEXT, event TEXT, detail TEXT,
  interface TEXT,                     -- cli | mcp | rpc | internal
  agent_host TEXT
);
```

FTS5 tables and the full index list are in `docs/PERFORMANCE.md` §3 — use those definitions verbatim.

**Rules.** Tag↔flag mapping is bidirectional: `\Seen`↔absence of `unread`, `\Flagged`↔`flagged`, `\Answered`↔`replied`, `\Draft`↔`draft`. Custom IMAP keywords map to same-name tags. Threading v0.1 uses normalized `References`/`In-Reply-To` root, falling back to normalized subject + participants, behind `IThreader`. Migrations are embedded, forward-only, keyed on `PRAGMA user_version`.

### 5.4 Provider abstraction

```csharp
public interface IMailProvider : IAsyncDisposable
{
    Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct);
    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct);
    IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderState state, CancellationToken ct);
    Task<byte[]> FetchRawMessageAsync(FolderRef folder, long uid, CancellationToken ct);
    Task SetFlagsAsync(FolderRef folder, long uid, FlagDelta delta, CancellationToken ct);
    Task MoveAsync(FolderRef from, long uid, FolderRef to, CancellationToken ct);
    Task SendAsync(MimeMessage message, CancellationToken ct);
    Task WatchAsync(FolderRef folder, Func<Task> onChange, CancellationToken ct);
}
```

`SyncEvent` union: `EnvelopeAdded`, `FlagsChanged`, `MessageExpunged`, `UidValidityChanged`.

### 5.5 Sync algorithm **[DECIDED]**

Planning is a pure function (`SyncPlanner.Plan`) — see `docs/ARCHITECTURE.md` §12.4. Execution:

1. `SELECT` folder; read UIDVALIDITY, HIGHESTMODSEQ, capabilities.
2. UIDVALIDITY changed → invalidate folder's UIDs, full re-fetch of envelopes (blobs re-link by sha256).
3. Else QRESYNC → `SELECT ... (QRESYNC (uidvalidity highestmodseq))`, apply VANISHED + FETCH deltas.
4. Else CONDSTORE → `UID FETCH 1:* (FLAGS) (CHANGEDSINCE modseq)` + `UID SEARCH UID uidnext:*`.
5. Else full UID-range diff; log `degraded_sync`.
6. Batch envelope fetch (≤500 UIDs per FETCH): ENVELOPE + BODYSTRUCTURE + FLAGS + SIZE.
7. Bodies lazy: fetched on `message.get`. On fetch, store blob, extract plaintext, fill `body_text`, update FTS, set `body_fetched=1`.
8. Persist folder state atomically with the batch — one transaction per batch, idempotent on re-run.
9. `WatchAsync` = IMAP IDLE, re-issued every 9 minutes (see `RELIABILITY.md`).

**Send:** build `MimeMessage`; **pre-assign `Message-ID` at outbox creation**; SMTP submission; on success APPEND to Sent unless server auto-saves. On startup, reconcile any row stuck in `sending` against the Sent folder before retrying — never double-send.

### 5.6 JSON-RPC surface v0.1 **[DECIDED]**

JSON-RPC 2.0 over stdio with **`Content-Length` framing (LSP-style)** so `vscode-jsonrpc` works unmodified. All DTOs in one source-generated `JsonSerializerContext`.

| Method | Params → Result |
|---|---|
| `initialize` | `{clientName, clientVersion, protocolVersion:1}` → `{daemonVersion, protocolVersion, capabilities}` |
| `secret.set` | `{ref, value}` → `{}` — straight to keyring, never logged |
| `account.add` | `{email, provider, imap{...}, smtp{...}, auth{kind}, secretRef}` → `{accountId}` |
| `account.list` | `{}` → `{accounts:[...]}` |
| `folder.list` | `{accountId}` → `{folders:[{id,name,role,unread,total}]}` |
| `sync` | `{accountId, folderId?}` → `{added,updated,expunged,durationMs}` |
| `search` | `{query, limit?, cursor?}` → `{hits:[EnvelopeDto], nextCursor?, truncated}` |
| `thread.get` | `{threadKey}` → `{messages:[EnvelopeDto]}` |
| `message.get` | `{messageId, format:'text'\|'html'\|'raw', fetchIfMissing:true}` → `{envelope, bodyText?, bodyHtml?, attachments:[...]}` — `bodyHtml` is **raw**; sanitization is the client's job |
| `attachment.get` | `{messageId, index}` → `{filename, mime, base64}` |
| `tags.set` | `{messageId, add:[], remove:[]}` → `{tags:[]}` |
| `message.move` | `{messageId, toFolderId}` → `{}` |
| `send.preview` | `{accountId, draft}` → `{preview, confirmToken}` |
| `send` | `{accountId, draftId, confirmToken}` → `{messageId}` — **rejects without a valid token**; logs to `sync_log` |
| `watch.subscribe` | `{accountId}` → `{}`; notifications `notify.mail.added`, `notify.folder.updated`, `notify.sync.error` |
| `stats` / `health` | `{}` → metrics (see `RELIABILITY.md`) |
| `shutdown` | `{}` → `{}` |

Error codes are stable and numeric (`1000` auth, `1001` network, `1002` not-found, `1003` confirm-required, `1004` store-corrupt, `1005` rate-limited). Document each in `docs/rpc.md` when introduced.

### 5.7 Secrets **[DECIDED]**

`ISecretStore` with `WindowsCredentialManagerStore`, `MacKeychainStore`, `LibSecretStore`, and `EncryptedFileStore` (DPAPI on Windows / scrypt+ChaCha20 elsewhere) as fallback — the fallback is **required**, not optional, because headless/Docker has no keyring. Secrets referenced by `secretRef`; raw values never appear in `config_json`, logs, RPC responses, or exception messages.

### 5.8 Native AOT rules **[DECIDED]**

`PublishAot=true`, `InvariantGlobalization=false` (MIME charsets), `PredefinedCulturesOnly=true`, `InvariantTimezone=false`, `StripSymbols=true`. **No S/MIME/PGP/BouncyCastle** — the known AOT gap and out of scope anyway. STJ source generators only. Any new package must pass the AOT CI job. Full size-tuned property block in `docs/DEPENDENCIES.md`.

### 5.9 Windows & Maildir specifics

SQLite blob store is primary everywhere in v0.1. Maildir is import/export only (v0.2): POSIX writes `:2,<flags>`; Windows writes the `!` separator variant and never `:` on Windows paths. Windows docs must mention `LongPathsEnabled` and an optional Defender exclusion. Folder names are case-preserving, compared case-insensitively on Windows.

---

## 6. Extension specification

### 6.1 Stack & packaging **[DECIDED]**
TypeScript + esbuild, **no native node modules**. Deps: `vscode-jsonrpc`, `dompurify` (bundled into the webview script). `"extensionKind": ["workspace"]` — must run where the store lives (Remote-WSL/SSH). Declare limited untrusted-workspace support (read-only; no send). Minimal `activationEvents`. Publish to **both** Marketplace and Open VSX with the same publisher ID, as **platform-specific VSIX targets** (`win32-x64`, `darwin-arm64`, `darwin-x64`, `linux-x64`) — never one fat multi-RID VSIX. Display name must not imply Microsoft affiliation.

### 6.2 UI surfaces
1. **Sidebar (`TreeDataProvider`)** — accounts → saved searches (`Inbox`, `Unread`, `Flagged`) → folders, with unread badges from `folder.list`.
2. **Message list** — TreeView in v0.1 (webview list is a v0.2 upgrade), fed by `search` with cursor paging (page size 50); threads collapse by `thread_key`.
3. **Reader (Webview)** — plaintext by default; "View HTML" renders sanitized; attachment list with save-as; actions reply/archive/tag.
4. **Compose** — untitled buffer with a header block (`To:`, `Cc:`, `Subject:`, `---`); `mailcoded.send` parses, shows a confirmation QuickPick with recipients + subject, then calls `send.preview` → `send`.
5. **Status bar** — sync state and last error; click to `sync`.
6. **Onboarding** — `mailcoded.addAccount` QuickInput: email, host autodetect, app-password paste → `secret.set` → `account.add` → initial `sync` with progress.

### 6.3 Security requirements (webview) — **hard invariants**
- Strict CSP on every webview: `default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-<nonce>'; img-src data:;` — **no remote origins**. Show a "N remote images blocked" bar with an explicit per-message allow that swaps `img-src` for that render only.
- All HTML mail passes DOMPurify **inside the webview** before injection: `FORBID_TAGS: ['script','iframe','object','embed','form','link','meta','base']`, all `on*` in `FORBID_ATTR`, URL scheme allowlist (`https`, `mailto`).
- Links open via `vscode.env.openExternal` only. Never render `bodyHtml` outside the sandboxed webview; never `eval`; never pass mail content into any command or terminal.
- Any change to sanitization or CSP is security-sensitive: add tests and flag it in the PR description.

### 6.4 Performance rules
The mail store is never inside a workspace folder; the extension never uses VS Code file watchers on mail data (all change signals arrive as daemon notifications). Ship recommended `files.watcherExclude`/`search.exclude` snippets in the README. List paging only — never `search` without a limit. Reader fetches bodies on demand.

---

## 7. Agent integration + safety **[DECIDED — non-negotiable]**

Full design in `docs/AGENT-INTERFACE.md`. Summary of the invariants:

Threat model: incoming email is attacker-controlled input. An agent with mail access holds all three legs of the lethal trifecta (private data, untrusted content, external communication) — cf. EchoLeak (CVE-2025-32711), a zero-click prompt injection against M365 Copilot. Therefore:

- **Layer 1 (primary): the CLI.** Shell-capable agents (Claude Code, Codex CLI, Gemini CLI, opencode, Goose) use `mailcoded <verb> --json`. Near-zero token overhead.
- **Layer 2: `SKILL.md`** bundled in the repo — portable across Agent Skills–compatible hosts.
- **Layer 3 (M5): thin MCP adapter** (`mailcoded --mcp`, official C# SDK, stdio) for hosts that cannot run a shell. Tools map 1:1 to CLI verbs.
- **No delete/expunge/trash tool exists in any layer.** Absent, not gated.
- **Agents receive plaintext bodies only** — never HTML.
- **Send** requires: `MAILCODED_SEND=1`, a two-phase preview→one-time-token exchange, all recipients matching `MAILCODED_APPROVED_RECIPIENTS`, ≤5 sends/hour, and a `sync_log` entry per attempt.
- **Raw SQL read** (`mailcoded query --sql`) is off unless `MAILCODED_ENABLE_SQL=1`; always `PRAGMA query_only`, always row-capped. Agents never get a raw file handle to the DB, Maildir, or blob store.
- **Gates live in `Mailcoded.Core`**, never in an adapter.

---

## 8. Testing strategy

- **Unit (Core):** store CRUD + migrations; tag↔flag mapping (property-based, both directions); FTS indexing incl. CJK and diacritics; threader; every file in `fixtures/eml/` parses without throwing. Assert FTS5 present via `PRAGMA compile_options`.
- **Sync state machine:** table-driven tests on the pure planner covering the whole edge-case matrix in `RELIABILITY.md` — UIDVALIDITY flip, QRESYNC delta, VANISHED, flag conflict, mid-batch crash idempotency. Zero mocks.
- **Integration:** Testcontainers Dovecot + smtp4dev — add account → sync → search → tag → verify flag on server → send → APPEND-to-Sent verified. Must pass before any release tag.
- **Daemon:** golden-file JSON-RPC transcripts incl. framing, error codes, confirm-required on `send`.
- **AOT smoke:** per-RID spawn/initialize/shutdown in CI.
- **Extension:** unit tests against mock daemon transcripts; one `@vscode/test-electron` smoke.
- Coverage gate: Core ≥ 80% lines. No gate on hosts.

---

## 9. Milestones & acceptance criteria

Work strictly in order. A milestone is done only when every box checks.

**M0 — Scaffold**
- [ ] `dotnet build` + `dotnet test` green on Linux/macOS/Windows CI.
- [ ] AOT publish matrix produces runnable binaries; stdio smoke test (initialize/shutdown) passes on all three OSes.
- [ ] Startup check asserts FTS5; failure exits with an actionable error.
- [ ] `flake.nix` devShell builds the solution on NixOS-WSL.
- [ ] Architecture tests from `ARCHITECTURE.md` §12.7 present and green.

**M1 — Store + MIME**
- [ ] Schema v1 + migrations; all `fixtures/eml/*` parse, index, and are searchable by subject/from/body.
- [ ] Tag↔flag mapping unit-tested both directions; `sync_log` written on every mutation.
- [ ] `mailcoded import-eml <dir>` loads fixtures; `mailcoded search "term" --json` returns hits.

**M2 — IMAP sync + send**
- [ ] `account.add` (password auth) against Dovecot container; initial sync of a 5k-message folder < 60 s on CI; re-sync no-op < 2 s (QRESYNC path exercised).
- [ ] UIDVALIDITY-change test passes (invalidate + recover, no duplicate blobs).
- [ ] Lazy body fetch on `message.get`; FTS row upgraded with body text.
- [ ] Outbox: `send` without a valid token → error 1003; with token delivers via SMTP sink; crash-after-250 reconciliation test passes (no double-send).
- [ ] IDLE watch emits `notify.mail.added` within 5 s of external APPEND.

**M3 — Daemon surface + secrets + extension skeleton**
- [ ] Full §5.6 surface with golden-file tests; TS types generated and consumed by the extension.
- [ ] Keyring store works on all three OSes; `EncryptedFileStore` fallback tested headless.
- [ ] Extension activates, spawns/locates the daemon, sidebar shows accounts→folders with unread badges; onboarding adds a real account end-to-end.

**M4 — Read/act/compose in the extension**
- [ ] Message list pages through 10k messages smoothly; open message < 300 ms warm.
- [ ] Reader: plaintext default; HTML toggle sanitized; remote images blocked with count bar + per-message allow; zero CSP violations during a 50-message browse.
- [ ] Tag/archive round-trips to server; compose→confirm→send works; reply quotes and sets `In-Reply-To`.
- [ ] Untrusted-workspace mode: send disabled, read works.

**M-perf — Benchmarks (runs alongside M4)**
- [ ] Seeded 500k-envelope synthetic corpus generator committed (fixed seed, ~10% CJK).
- [ ] BenchmarkDotNet gates on the reference machine: envelope page p95 < 10 ms; FTS p95 < 100 ms; unread badge < 5 ms; thread view < 20 ms; ingest ≥ 2,000 env/sec.
- [ ] Regression >15% vs the committed baseline fails the build.

**M5 — Polish + agents + packaging**
- [ ] SKILL.md shipped; CLI verbs complete with versioned JSON and exit-code conventions.
- [ ] MCP adapter passes gate tests (send blocked without env+token+allowlist; **no delete tool present in the tool list**).
- [ ] `vsce package` and Open VSX dry-run clean; icon/banner/screenshots/30s GIF committed; README with comparison table + honest platform matrix + `docs/agents.md`.
- [ ] Backend v0.1.0 tagged; GitHub Release with per-RID binaries + SHA-256; extension pins that version.

**M-chaos / M-soak — before v0.1.0 tag**
- [ ] Toxiproxy fault-injection suite (latency, resets, blackholes) green.
- [ ] 24h+ soak: idle RSS < 50 MB, active < 150 MB, stable handle count, bounded WAL, no upward heap trend.

**M6 — Launch** (non-code; checklist in `docs/launch.md`) — Show HN, r/vscode + r/emacs + r/neovim, newsletters, awesome-list PRs. Claude Code drafts; a human posts.

**v0.2** — GraphProvider (MSAL device-code, `messages/delta`, `sendMail`, ≤4 concurrent + honor `Retry-After`) and the `--listen` transport with full auth hardening.

---

## 10. Risks (top 5)

1. **Two-products trap** — backend polish delays the extension past the empty-niche window. → Milestone order is binding; no backend feature beyond §3.1 before M5 ships.
2. **MailKit single-maintainer** — pin versions; wrap all MailKit types behind Core interfaces so a fork is contained.
3. **AOT regression from a new dependency** — the AOT CI job is a required check.
4. **HTML-mail security bug** — CSP + DOMPurify invariants (§6.3); reader changes need a security review pass.
5. **Graph tenant/licensing wall** — document clearly; failures must produce actionable errors, never silent empty folders.

---

## 11. Reference index
MailKit `ExchangeOAuth2.md` / `GMailOAuth2.md` · Microsoft Learn "delta query messages" and Graph throttling limits (Outlook: 10k req/10 min, 4 concurrent) · Exchange Team blog "Retirement of Exchange Web Services" (phased from 2026-10-01) · Microsoft Learn "Microsoft.Data.Sqlite — Custom SQLite versions" · sqlite.org `fts5.html`, `wal.html`, `pragma.html` · VS Code Webview CSP guide, `extensionKind`, TreeView API, vsce + Open VSX publishing · modelcontextprotocol.io · agentskills.io specification.

---

## Appendix A — Naming
`mailcoded` (product, CLI command), `mailcoded` (daemon), `Mailcoded` (GitHub org), `Mailcoded.*` (NuGet), `@mailcoded/protocol` (npm). Obsidian store name (v0.3) must be "Mailcoded" — no "Obsidian" or "Plugin" in the name. Do not use "VS Code" in the extension's name.

## Appendix B — Deferred (v0.3+)
JMAP provider; Neovim client over the same JSON-RPC; Maildir live-interop with notmuch on POSIX; webview message list; S/MIME as a framework-dependent optional build; Gmail API provider (labels-native); attachment-content indexing.
