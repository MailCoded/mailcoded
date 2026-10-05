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
- FTS5 search, notmuch-compatible tags (`unread`, `flagged`, `replied`, custom). Search leaves out Trash- and Junk-role Folders unless the query names a Folder, says `in:anywhere`, or is scoped to one Folder.
- Attachment text search, narrowly: plain-text-like attachments (`text/plain`, `text/csv`, `text/tab-separated-values`, `text/markdown`, `text/calendar`, `application/json`, `text/html`) and Word `.docx` files are read for text when a message body is fetched or imported, and a background pass covers bodies fetched earlier. The `attachment:` operator searches that text; bare words never do. Nothing else is read: PDF, spreadsheets, presentations, images, legacy `.doc` and archives are not indexed. Extraction uses the .NET base class library only and treats every container as hostile (byte, ratio, entry, character and time budgets; no XML parser, no DTD or entity expansion).
- Send via SMTP with mandatory confirmation + outbox state machine. A draft's From is the account address or a configured sender identity, checked in Core; plaintext signatures per account, with defaults for new messages and replies.
- Drafts saved to the server (opt-in per account, off by default): a draft is also appended to the server's Drafts Folder so other mail clients show it. The local draft stays the only source for a send. After a send the server draft is moved into Sent with one MOVE when the server supports it and the account keeps a Sent copy, and otherwise left where it is; mailcoded never deletes, flags for deletion or expunges server mail.
- Compose suggestions: the people you have written to, learned from outgoing mail only (mailcoded's own sends and messages in Sent-role Folders you wrote), ranked by frequency and recency. Received mail never adds anyone. This is not a contacts feature.
- Message signals, all derived from headers and MIME structure the sender controls, so they sort mail and never vouch for it: `automated` (list and bulk headers, auto-replies, delivery and read reports, robot senders), `encrypted` (PGP/MIME, S/MIME enveloped data, inline-armored PGP; mailcoded never decrypts), and `dmarc` (a result counts only when it comes from an `Authentication-Results` instance whose authserv-id the Account trusts, and names the single From domain). Search operators `is:automated`, `is:encrypted`, `is:verified`. Unknown is a stored state, not false. Four local-only category Tags (`auto.social`, `auto.news`, `auto.marketing`, `auto.notifications`) are assigned once from fixed rules; this is not a user-defined rules engine (§3.3).
- Store backup: `backup <dir>` and `backup-verify <dir>` (RPC `store.backup`, `store.backupVerify`) write and re-check a consistent copy of the store plus its external blob files and a `manifest.json`. Secrets are never copied. Restore is a documented manual procedure, not a verb.
- `notify.sync.progress`: counts and Folder identifiers while a sync runs, on request (`sync` with `progress: true`, or `watch.subscribe`), throttled per Account; the TUI and the CLI show it.
- Folder hierarchy: each Folder carries its server delimiter, leaf name, depth and parent on the wire; the CLI `folders` verb and the TUI sidebar render a tree.
- `List-Unsubscribe`: the header is parsed strictly and stored; `unsubscribe <id>` turns a `mailto:` option into an ordinary draft that still goes through the two-phase send. An HTTPS one-click POST is refused by design and never fetched: the URL is sender-chosen and the network rule allows mail protocols only.
- IMAP connections: IDLE already runs on its own connection per watched Folder; an interactive overflow connection serves body and attachment fetches, Flag and Tag pushes, moves and appends while the primary connection is busy streaming a sync, within a per-Account connection budget.
- JSON-RPC daemon (stdio) + thin one-shot CLI (`mailcoded`, JSON output).
- Secrets in OS keyring with encrypted-file fallback.
- VS Code extension: sidebar, threaded list, reader (plaintext-first, sanitized HTML toggle, remote images blocked), tag/archive, compose/reply/send.
- MCP adapter: read/search/draft; send gated; **no delete tools exist**.
- SKILL.md for shell-capable agents.

### 3.2 In scope — v0.2
- **GraphProvider**: MSAL.NET device-code flow, delta queries, `Mail.Read`/`Mail.Send`. Flagship differentiator.
- Gmail OAuth2, multi-account.
- A native Gmail REST provider was proposed with the M2a set and **not built**: CLAUDE.md's network rule allows only mail protocols and the Graph/MSAL endpoints, v0.2 builds Graph first, and there is no Google OAuth client to test against. Gmail is reached over IMAP with an app password today.
- **Localhost HTTP/WS + Unix-socket/named-pipe listener** (`mailcoded --listen`) with full auth hardening — the keystone that unlocks every other client. See the family table in `ROADMAP.md`.
- Maildir export/import (POSIX `:`, Windows `!`), optional notmuch interop on POSIX.

### 3.3 Explicitly out of scope (v0.x)
POP3, JMAP (v0.3+), calendars/contacts (the Correspondent suggestion list in §3.1 is derived from sent mail and is not an address book), PGP, S/MIME (out of scope for v0.x; decrypted content must never reach an agent or the store, see `docs/AGENT-INTERFACE.md` §13.6), HTML compose, attachment upload in composer, mobile/web clients, IMAP server mode, message-rules engine, attachment-content indexing beyond the text-like types and Word `.docx` listed in §3.1 (PDF, spreadsheets, presentations, images and OCR stay out; disclose this gap in the README as roadmap).

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
  delimiter TEXT,                    -- server hierarchy delimiter; '' = flat (NIL), NULL = not yet listed, read as '/' (migration 012)
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
  automated INTEGER,                  -- 1 yes, 0 no, NULL unknown (migration 009)
  encrypted INTEGER,                  -- 1 PGP/MIME, S/MIME envelope or inline armor; NULL unknown
  dmarc TEXT,                         -- pass|fail|none|unknown from a trusted Authentication-Results; imported = import-eml file, reads as unknown; NULL not evaluated (migration 010)
  unsubscribe TEXT,                   -- sanitized List-Unsubscribe, LF, List-Unsubscribe-Post; '' = headers seen, none offered; NULL unknown (migration 011)
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

**Rules.** Tag↔flag mapping is bidirectional: `\Seen`↔absence of `unread`, `\Flagged`↔`flagged`, `\Answered`↔`replied`, `\Draft`↔`draft`. Custom IMAP keywords map to same-name tags, except the reserved local-only `auto.` prefix: the four built-in category Tags (`auto.social`, `auto.news`, `auto.marketing`, `auto.notifications`) are assigned once at first insert from fixed rules, never pushed as keywords and never taken from one. Threading v0.1 uses normalized `References`/`In-Reply-To` root, falling back to normalized subject + participants, behind `IThreader`. Migrations are embedded, forward-only, keyed on `PRAGMA user_version`.

**Correspondents.** `correspondents(account_id, address, display_name, use_count, last_used_utc)` holds, per Account, the people the user has written to: the lower-cased address, the display name on the most recent message that carried one, how many outgoing messages named it in To or Cc, and when. It is learned from outgoing mail only, never from received mail: once per Outbox row, in the same transaction that marks the row `sent` (so retries and crash reconciliation cannot count it twice); and once per Message newly inserted into a Sent-role Folder whose From is the Account's address or a configured identity, unless an Outbox row owns that Message-ID. Bcc is not counted; the Account's own addresses, robot senders and messages to more than 25 distinct recipients are skipped. The rows go with the Account (`ON DELETE CASCADE`). Sent mail already stored when the table first exists is counted once, when the store next opens.

**Additions to the decided schema.** The block above is the v1 schema plus the `messages` and `folders` columns marked with their migration. Everything else added by the owner-approved set (§9 M2a) is additive and forward-only, never edits an applied migration, and none of it holds a secret:

| Migration | Adds |
|---|---|
| 008 | `msg_vec`, `vec_model` (search by meaning; before the M2a set) |
| 009 to 011 | `messages.automated`, `encrypted`, `dmarc`, `unsubscribe` (nullable; NULL means unknown or not evaluated, and is not backfilled in bulk: a row fills in when its body is fetched or read again) |
| 012 | `folders.delimiter` |
| 013 | `correspondents`, `correspondent_backfill` (§5.3 Correspondents) |
| 014 | `outbox.server_draft_folder`, `server_draft_uidvalidity`, `server_draft_uid`, `server_draft_state` (`saved`, `moving`, `moved`, `left`), `server_draft_reason` |
| 015 | `attachment_text` (extracted text per attachment, keyed `message_id, idx`), `attachment_index` (extractor version per message, the backfill queue), `att_fts` (porter) and `att_fts_cjk` (trigram), both contentless with `contentless_delete=1`, and the partial index `ix_msg_attachment_queue` |
| 016 | `correspondent_learned` (one row per outgoing Message-ID already counted, so a UIDVALIDITY re-enumeration cannot count a Sent message twice) |
| 017 | partial index `ix_msg_encrypted` over `messages` where `encrypted = 1` |

`accounts.config_json` also carries, only when set: `trustedAuthservIds`, `identities`, `signatures` with `defaultSignature`, and `saveDraftsToServer`. The category Tags use the existing `tags` table.

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
| `folder.list` | `{accountId}` → `{folders:[{id,name,role,unread,total,delimiter,leaf,depth,parentId}]}`, in tree order |
| `sync` | `{accountId, folderId?, progress?}` → `{added,updated,expunged,durationMs}` |
| `search` | `{query, limit?, cursor?}` → `{hits:[EnvelopeDto], nextCursor?, truncated}`; operators in `docs/rpc.md`; a hit may carry `automated`, `encrypted`, `dmarc` and `attachmentMatch` |
| `thread.get` | `{threadKey}` → `{messages:[EnvelopeDto]}` |
| `message.get` | `{messageId, format:'text'\|'html'\|'raw', fetchIfMissing:true}` → `{envelope, bodyText?, bodyHtml?, attachments:[...]}` — `bodyHtml` is **raw**; sanitization is the client's job; also `unsubscribe` (parsed `List-Unsubscribe` options, never acted on) |
| `attachment.get` | `{messageId, index}` → `{filename, mime, base64}` |
| `tags.set` | `{messageId, add:[], remove:[]}` → `{tags:[]}` |
| `message.move` | `{messageId, toFolderId}` → `{}` |
| `send.preview` | `{accountId, draft}` → `{preview, confirmToken}` |
| `send` | `{accountId, draftId, confirmToken}` → `{messageId}` — **rejects without a valid token**; logs to `sync_log` |
| `watch.subscribe` | `{accountId}` → `{}`; notifications `notify.mail.added`, `notify.folder.updated`, `notify.sync.error`; `notify.sync.progress` also for `sync` with `progress: true` |
| `stats` / `health` | `{}` → metrics (see `RELIABILITY.md`) |
| `correspondent.suggest` | `{accountId, prefix?, limit?}` → `{correspondents:[{address,displayName?,count,lastUsedUtc}]}` — people you have written to, best first (frequency and recency), for completing a recipient; learned from outgoing mail only |
| `provider.detect` | `{email}` → `{preset}` — built-in IMAP/SMTP settings and credential guidance; pure lookup, no network |
| `store.backup` | `{directory}` → `{backup, secretsIncluded:false}` — a consistent copy of `store.db` (`VACUUM INTO`), the external blob files it points at, and a `manifest.json`; never secrets; gated (CLI needs `MAILCODED_ALLOW_BACKUP=1`, MCP never); audited |
| `store.backupVerify` | `{directory}` → `{ok, problems[], ...}` — re-hashes the copy and compares it with its manifest; a mismatch is a result, not an error |
| `shutdown` | `{}` → `{}` |

Error codes are stable and numeric (`1000` auth, `1001` network, `1002` not-found, `1003` confirm-required, `1004` store-corrupt, `1005` rate-limited, `1006` forbidden, `1007` store-full, `1008` unsupported). Document each in `docs/rpc.md` when introduced. A draft whose `from` is neither the account address nor a configured identity is `-32602` with `error.data.reason: "sender_not_allowed"`; no new numeric code was needed.

Notifications, all opt-in, none carrying a subject, address or body: `notify.mail.added`, `notify.folder.updated`, `notify.sync.error`, `notify.sync.progress`. This table is the decided surface; `docs/rpc.md` is the complete reference and also lists `account.test` and `outbox.list`, and the fields the M2a set added (`capabilities.backup`, `AccountDto` identities, signatures and `saveDraftsToServer`, `send.preview` and `send` server-draft outcome, `FolderDto` hierarchy).

### 5.7 Secrets **[DECIDED]**

`ISecretStore` with `WindowsCredentialManagerStore`, `MacKeychainStore`, `LibSecretStore`, and `EncryptedFileStore` (DPAPI on Windows / scrypt+ChaCha20 elsewhere) as fallback — the fallback is **required**, not optional, because headless/Docker has no keyring. Secrets referenced by `secretRef`; raw values never appear in `config_json`, logs, RPC responses, or exception messages.

### 5.8 Native AOT rules **[DECIDED]**

`PublishAot=true`, `InvariantGlobalization=false` (MIME charsets), `PredefinedCulturesOnly=true`, `InvariantTimezone=false`, `StripSymbols=true`. **No S/MIME/PGP/BouncyCastle** — out of scope for v0.x and unsafe for the agent threat model (§3.3); BouncyCastle is also kept out for the size budget; the AOT build is not the reason (the trimmer removes it, see `docs/DEPENDENCIES.md`). STJ source generators only. Any new package must pass the AOT CI job. Full size-tuned property block in `docs/DEPENDENCIES.md`.

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
- **Store backup** (`mailcoded backup`, `backup-verify`; RPC `store.backup`, `store.backupVerify`) is a second plaintext copy of the store, so it is off for the CLI agent surface unless `MAILCODED_ALLOW_BACKUP=1`, absent from MCP, audited, and never writes inside the live store or a workspace folder. Secrets are never copied.
- **Settings that widen trust** — `account trust` (and `--trust-authserv-id` on `account add` and `setup`), `account identity add`, `account server-drafts on` — need a terminal confirmation, or `--yes` with `MAILCODED_ALLOW_TRUST_CHANGE=1`, `MAILCODED_ALLOW_IDENTITY_CHANGE=1` or `MAILCODED_ALLOW_SERVER_DRAFTS=1`. Like `move` and `account forget`, the confirmation lives in the CLI command class, not in `AgentPolicy`; no RPC method or MCP tool changes any of them, and each change is audited.
- **Signals are not vouchers.** `automated`, `encrypted`, `dmarc` and the category Tags come from headers the sender controls; `is:verified` means a trusted server reported DMARC pass for the From domain, nothing more. Encrypted mail is marked and never decrypted; an agent reports it and does not try to read it (`docs/AGENT-INTERFACE.md` §13.6). An unsubscribe link is never opened by mailcoded or by an agent.
- **Gates live in `Mailcoded.Core`**, never in an adapter.

---

## 8. Testing strategy

- **Unit (Core):** store CRUD + migrations; tag↔flag mapping (property-based, both directions); FTS indexing incl. CJK and diacritics; threader; every file in `fixtures/eml/` parses without throwing. Assert FTS5 present via `PRAGMA compile_options`.
- **Sync state machine:** table-driven tests on the pure planner covering the whole edge-case matrix in `RELIABILITY.md` — UIDVALIDITY flip, QRESYNC delta, VANISHED, flag conflict, mid-batch crash idempotency. Zero mocks.
- **Integration:** Testcontainers Dovecot + smtp4dev — add account → sync → search → tag → verify flag on server → send → APPEND-to-Sent verified. Must pass before any release tag.
- **Daemon:** golden-file JSON-RPC transcripts incl. framing, error codes, confirm-required on `send`.
- **AOT smoke:** per-RID spawn/initialize/shutdown in CI.
- **Opt-in scale test:** with `MAILCODED_TEST_PERF=1` the normal suite seeds the 100,000-message corpus and checks the read paths against ceilings (about two minutes); unset, it reports itself skipped. It is a regression tripwire on a development machine, not the M-perf gate.
- **Hostile input:** any parser of untrusted input (headers, `Authentication-Results`, `List-Unsubscribe`, recipient lists, zip and XML in attachments, backup manifests) ships with adversarial cases: oversize, deeply nested, malformed, duplicated, and forged input that must never produce a positive result.
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

**M2a — Owner-added feature set** *(added by the owner's decision after M2 was written. It sits here because it is backend work on top of M2's sync, store and send, and it leaves the order of M3 onward unchanged; §10 risk 1 is amended to match. A ticked box means that check was run on this tree during this work and passed; `docs/verification.md` has the evidence and the list of what was not run. An unticked box says why.)*

Everything below, plus the daemon surface it touches, is described in §3.1, §5.3 and §5.6.

*All features*
- [x] `dotnet build Mailcoded.slnx` with `TreatWarningsAsErrors` is clean and the whole unit suite is green (counts in `docs/verification.md`).
- [x] Protocol DTO changes regenerate `packages/protocol/src/generated.ts` and the generator's `--check` reports it up to date; every new RPC behaviour has a golden transcript pair.
- [x] The Native AOT daemon published with zero IL2xxx/IL3xxx warnings on the tree after each of the signals, unsubscribe, backup, sync-progress, identities, server-drafts, correspondents and attachment-text work.
- [x] A Native AOT publish of the final source (daemon, CLI, TUI) has zero IL2xxx/IL3xxx or other warnings, passes the 20 MiB size gate and the stdio smoke test, and the AOT CLI returns the same search results as the JIT build over all 56 fixtures (sizes in `docs/DEPENDENCIES.md`).
- [x] The Docker integration tests for the new behaviour (15 test methods in nine files) pass against Dovecot and smtp4dev, together with the original five: 20 of 20.
- [ ] The macOS and Windows CI legs run the new code: not run.

*Message signals*
- [x] `is:automated`, `is:encrypted` and `is:verified` parse, compile and match as documented, including the unknown-matches-neither rule for `automated` and `encrypted` and the broad `-is:verified`; bounded header handling is covered by adversarial tests (control characters, duplicate and oversized headers, hostile display names, quoted and mid-line PGP armor).
- [x] `Authentication-Results` is parsed to RFC 8601 rules and a result counts only from an instance whose authserv-id the Account trusts and that names the single From domain; forged, planted, commented, quoted and conflicting headers never yield a pass in the tests; the trust list changes only behind a terminal confirmation or `MAILCODED_ALLOW_TRUST_CHANGE=1` with `--yes`; imported mail is never verified.
- [x] Category Tags are assigned once at first insert, never pushed as keywords, never taken from a server keyword, and a Tag the user removed stays removed.
- [x] Signals fetched from a real IMAP server (`HEADER.FIELDS` alongside the envelope items), against Dovecot: `is:automated`, `is:encrypted`, and an `is:verified` that only a trusted authserv-id can produce.
- [ ] Signals against any provider but Dovecot: not run. Only Gmail ships a built-in authserv-id; every other provider needs the human to configure one.

*Search*
- [x] Trash- and Junk-role Folders are left out unless the query names a Folder, says `in:anywhere`, or the call is Folder-scoped, on every search route including the semantic and relaxed retries.
- [x] `attachment:` searches extracted text only; bare words never reach it; `has:attachment` is unchanged.
- [ ] The M-perf gates with the new predicates (`m.folder_id NOT IN (...)`, the attachment join): the BenchmarkDotNet suite has never run. Ad hoc plan probes are in `docs/PERFORMANCE.md`.

*Attachment text*
- [x] Text-like attachments and `.docx` are read for text with the BCL alone; zip bombs, lying central directories, entry-count patches, path-traversal names, XXE, entity chains, DOCTYPE, truncated and corrupted archives are refused or bounded in the tests, and a failed attachment never fails a fetch or sync.
- [x] The `.docx` path is measured against an `XmlReader` alternative and the hand scanner is kept (`docs/DEPENDENCIES.md`).
- [x] Extraction on a fetched body and through the background backfill, against Dovecot: `.docx` and CSV attachment text becomes searchable.
- [ ] A human review of the parsers of untrusted input (`WordXmlTextScanner`, `DocxTextExtractor`, `AttachmentTextExtractor`): not done; they are flagged for it.

*Store backup*
- [x] `backup` and `backup-verify` (and `store.backup`, `store.backupVerify`) produce and check a manifest-described copy; each tampered, missing, extra or linked file is reported, a copy restored by hand opens, and a destination inside a workspace Folder, the live store or a symbolic link is refused.
- [x] The CLI is off for agents without `MAILCODED_ALLOW_BACKUP=1`, MCP has no tool and refuses even with it, and every attempt is audited. Secrets are never copied.
- [ ] A backup of a large live store under sustained writes, and on Windows or macOS: not run.

*Sync progress and connections*
- [x] `notify.sync.progress` is throttled per Account, closes every indicator it opened with one `final`, carries no subject, address or body, and is opt-in; the CLI line and the TUI status line are unit-tested.
- [x] Progress against a real server (Dovecot): folder, account and watched syncs report and close their indicators, and a sync that does not ask gets none.
- [ ] An interactive terminal run of the CLI progress line and the TUI: not run.
- [x] The interactive connection lane answers a fetch while a streaming sync holds the primary connection (router tests with the real command queue), falls back and backs off when a second connection is refused, and stays inside the per-Account budget.
- [x] The lane against a real server (Dovecot): a body fetch and a Flag push complete while a sync holds the primary connection, and wait for it without the lane.

*Folders*
- [x] The server delimiter is stored per Folder and the hierarchy (`delimiter`, `leaf`, `depth`, `parentId`) is on the wire, in `folders` and in the TUI sidebar; a `/` inside a segment under another delimiter is refused rather than mis-addressed, and reported once per refresh.
- [ ] A server whose delimiter is not `/`: not run against one.

*Sending*
- [x] A draft's From must be the account address or a configured identity, checked once in Core for every route; signatures follow RFC 3676 §4.3 placement; identity changes are gated like `account trust`.
- [x] A server draft is saved only when the Account opted in (gated like `account trust`), is never deleted or flagged for deletion, is moved into Sent with one `UID MOVE` only when every condition holds, and every crash window leaves the draft in place without a second move or a duplicate Sent copy.
- [x] Server drafts against a real server (Dovecot): visible to another client, moved into Sent by one `UID MOVE`, and left in place with the Sent copy appended after a UIDVALIDITY change.
- [x] `unsubscribe` drafts only a strictly parsed single-recipient `mailto:`, still goes through preview, token, allowlist and rate cap, and no code path fetches an HTTP URL.
- [x] Correspondent suggestions are learned from outgoing mail only, once per sent message, never from Bcc or received mail, and are ranked deterministically.

*Documentation and performance*
- [x] The crypto-exclusion rationale states scope and the agent threat model, not AOT, and the agent rule for encrypted mail is recorded in `docs/AGENT-INTERFACE.md` §13.6.
- [x] A 100,000-message read-path test is in the normal suite, skipped unless `MAILCODED_TEST_PERF=1`, and was run once on the development machine. Two paths (a common FTS term, the thread page) are above their scaled targets; that is recorded as open work, not hidden.
- [ ] A 100k or 500k result on the reference machine: not run, so no benchmark number is a reference-hardware claim.

*Not built*
- Native Gmail REST provider: not built (§3.2). IMAP covers Gmail.

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

1. **Two-products trap** — backend polish delays the extension past the empty-niche window. → Milestone order is binding; no backend feature beyond §3.1 before M5 ships. The owner-approved M2a set is the one deliberate exception; nothing further is added to the backend before M5 without the same decision.
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
JMAP provider; Neovim client over the same JSON-RPC; Maildir live-interop with notmuch on POSIX; webview message list; S/MIME as a framework-dependent optional build; Gmail API provider (labels-native; not built in the M2a set, see §3.2); attachment-content indexing for PDF, spreadsheets, presentations and images.
