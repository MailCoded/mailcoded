# RELIABILITY — SPEC §14: Resource budgets, long-run stability, edge-case matrix

**Status:** [DECIDED]

## 14.1 Resource budgets (asserted in the soak test)

| Metric | Target | Mechanism |
|---|---|---|
| Idle RSS | < 50 MB | Workstation GC, concurrent, no UI toolkit, AOT |
| Active-sync RSS | < 150 MB | streaming MIME parse, bounded fetch batches, ArrayPool |
| Idle CPU | ~zero polling | IMAP IDLE is push; only the IDLE re-issue + checkpoint timers fire |
| Cold start | single-digit to low-tens of ms | Native AOT |

These are engineering targets extrapolated from AOT service benchmarks (~17-23 MB working set for a minimal AOT service that opens sockets), **not measured for this stack**. Verify in M-soak before treating them as claims.

## 14.2 GC and AOT configuration

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <ServerGarbageCollection>false</ServerGarbageCollection>   <!-- single heap, lower idle -->
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  <EventSourceSupport>true</EventSourceSupport>              <!-- dotnet-counters under AOT -->
</PropertyGroup>
<ItemGroup>
  <!-- AOT does NOT read runtimeconfig.template.json -->
  <RuntimeHostConfigurationOption Include="System.GC.HeapHardLimitPercent" Value="25" Trim="false" />
</ItemGroup>
```

DATAS is a Server-GC feature and a no-op under Workstation GC — leave it alone. **`dotnet-gcdump`/PerfView do not work under Native AOT**, so leak-hunting uses either a non-AOT debug build or `GC.GetGCMemoryInfo()` diffs exposed through the `stats` RPC.

**Allocation discipline:** parse with `MimeParser`/`MimeMessage.Load(Stream)` — never materialize a whole-message `byte[]`. Fetch summaries in batches of ~500 UIDs. Pool buffers >= 85,000 bytes (the LOH threshold) via `ArrayPool<byte>.Shared` / RecyclableMemoryStream, or stream to a temp file. Use `System.IO.Pipelines` for the stdio framing.

## 14.3 SQLite configuration

```sql
PRAGMA journal_mode  = WAL;
PRAGMA synchronous   = NORMAL;        -- corruption-safe under WAL
PRAGMA busy_timeout  = 5000;          -- absorbs AV locks + writer contention
PRAGMA cache_size    = -16384;        -- 16 MB (negative = KiB)
PRAGMA temp_store    = MEMORY;
PRAGMA mmap_size     = 268435456;     -- 256 MB; disable on network FS
PRAGMA foreign_keys  = ON;
PRAGMA wal_autocheckpoint = 1000;
PRAGMA journal_size_limit = 67108864; -- cap -wal reuse at 64 MB
```

`synchronous=NORMAL` under WAL is consistent; it can lose the last transaction on power loss, which is fine because IMAP re-supplies it. **Connection model:** one long-lived writer with all mutations serialized through a single queue, plus a small reader pool (WAL supports many readers). Never `Cache=Shared` with WAL. Never mmap a network filesystem.

**Long-term health.** WAL growth comes from checkpoint starvation by long-lived read transactions — keep read transactions short, run `PRAGMA wal_checkpoint(TRUNCATE)` after big syncs and periodically when idle. Run `PRAGMA quick_check` weekly. Prefer `auto_vacuum=INCREMENTAL` + chunked `incremental_vacuum` over full `VACUUM` (2x disk + exclusive lock); reserve `VACUUM INTO` for the `backup` RPC. FTS5: run `INSERT INTO msg_fts(msg_fts) VALUES('optimize')` after large churn; keep write batches modest so a crisis-merge doesn't stall a user-visible write.

**The safety valve: IMAP is the source of truth.** On corruption, drop the local DB and re-sync. The only unrecoverable local state is **tags** and the **outbox** — back those up separately (small sidecar DB or periodic export).

## 14.4 Long-running stability

**Connection lifecycle.** `client.IsConnected` can be stale after a network drop — do not trust it as a liveness probe. `ImapProtocolException` and `IOException`/`SocketException` → reconnect; `ImapCommandException` → per-command handling. Reconnect with exponential backoff + full jitter: 1s → 2s → 4s → 8s … capped at 5 min, reset after a stable 10 min. Re-authenticate on every reconnect. **Count auth failures separately from network failures** — never tight-loop a hard `AUTHENTICATIONFAILED` (server lockout / fail2ban); surface `auth-required` instead.

**Threading model.** MailKit is one-command-at-a-time per client. Use a dedicated `ImapClient` per IDLE-watched folder plus a per-account serialized command queue for on-demand fetches. Watch INBOX (and a small set) only — Gmail allows ~15 connections per account.

**IDLE cadence.** Re-issue every **9 minutes** for Gmail/consumer servers (Gmail drops idle connections around 10 min); RFC 2177 permits up to 29 minutes where tolerated. Coalesce into one scheduler, not per-folder timers.

**Suspend/resume.** After resume sockets are silently dead. `NetworkChange` events and OS power notifications are inconsistent cross-platform — treat them as hints. **Invariant:** any IDLE/command timeout triggers reconnect, AND a repeating monotonic timer (~60 s) compares `Environment.TickCount64` deltas against wall-clock deltas; a large divergence means the machine slept, so tear down and reconnect proactively.

**Daemon lifecycle.** Exit on stdin EOF (VS Code closes the pipe), plus a parent-PID watchdog (~30 s) against orphaning. **Single instance:** multiple VS Code windows must NOT each spawn a daemon — duplicate IMAP connections blow Gmail's 15-connection cap and duplicate the 2,500 MB/day download quota. Use a lock file holding PID + endpoint, a Unix domain socket (macOS/Linux) or named pipe (Windows) for handoff, PID-liveness check, and atomic takeover. Additional windows become RPC clients of the one daemon.

**Leak guards.** Unsubscribe MailKit events on disposal (`CountChanged`, `MessageExpunged`, `MessageFlagsChanged`); dispose every client; dispose `CancellationTokenSource`s per IDLE cycle; watch MSAL token-cache growth.

**OAuth.** Refresh tokens expire or get revoked (Google: unused 6 months, password change with Gmail scopes, or 7 days while the consent screen is in Testing; Microsoft: sliding windows + revocation). On silent-refresh failure emit an `auth-required` notification — never crash-loop. Persist the token cache encrypted per-OS.

**Send crash-safety.** `queued → sending → sent | failed`, **`Message-ID` pre-assigned at creation**. Dangerous window: crash after SMTP 250 but before DB commit → on startup, reconcile any row stuck in `sending` against the Sent folder (and by Message-ID) before re-sending. 451/4xx greylisting → backoff retry (1, 5, 15, 30 min) capped at 24 h; 421 → reconnect; 5xx → permanent failure surfaced with the enhanced status code. Pre-check the server `SIZE` capability.

## 14.5 Edge-case matrix

Every row needs a fixture and a table-driven test against the pure planner.

| # | Case | Detection | Behavior |
|---|---|---|---|
| 1 | UIDVALIDITY changed between sessions | stored vs `folder.UidValidity` | discard folder UIDs, full re-sync; blobs re-link by sha256 |
| 2 | UIDVALIDITY changes mid-session | QRESYNC open returns changed validity | abort incremental, re-open, resync |
| 3 | UIDNEXT backwards / UID reuse | UID <= max known but sha256 differs | trust sha256, not UID; log quirk |
| 4 | Advertises CONDSTORE/QRESYNC but broken (MODSEQ non-monotonic, no VANISHED, HIGHESTMODSEQ=0) | MODSEQ non-increasing or expunge missed | fall back to full FLAGS + SEARCH diff; set per-server quirk flag |
| 5 | Gmail label duplication (All Mail + Inbox + labels) | same `X-GM-MSGID`/sha256 across folders | store blob once; folders modelled as tags; thread via `X-GM-THRID` |
| 6 | Gmail has no real `\Deleted` | namespace + special-use | archive = remove `\Inbox`, never expunge |
| 7 | Access token expires mid-IDLE (3600 s on M365) | reconnect → AUTHENTICATIONFAILED | refresh proactively before expiry; reconnect cadence < token lifetime |
| 8 | Server closes connection after N commands | IOException / timeout | reconnect with backoff |
| 9 | iCloud advertises QRESYNC but Open throws | exception on QRESYNC open | fall back to CONDSTORE-only |
| 10 | ProtonBridge localhost self-signed TLS on :1143 | cert validation callback | allow user-pinned self-signed for localhost bridge only |
| 11 | Yahoo requires IMAP `ID`; low connection cap | server rejects until ID sent | send `client.Identify()`; cap watchers |
| 12 | Missing/duplicate Message-ID | header absent/duplicated | synthesize thread key from References/Subject+Date; dedupe by sha256 |
| 13 | Raw 8-bit / non-RFC2047 headers, unknown charset | MimeKit decode fallback | set `ParserOptions.CharsetEncoding`; register CodePages provider |
| 14 | 100 MB+ attachment | size from BODYSTRUCTURE | stream to content-addressed disk store above 512 KB; never buffer whole |
| 15 | Zero-byte / HTML-only / nested rfc822 / TNEF / calendar / S/MIME opaque | MimeKit part types | never crash; extract text where possible; store raw regardless; skip crypto |
| 16 | Header injection (CR/LF in address) | MimeKit validation (>=4.15.1) | reject at compose; never emit |
| 17 | CJK not segmented by porter/unicode61 | tokenizer test | dual index: unicode61 (Latin BM25) + trigram (CJK substring); LIKE fallback for <3-char CJK |
| 18 | Missing/invalid Date, far-future/1970 | parse failure / outlier | fall back to INTERNALDATE; clamp for display |
| 19 | System clock jump / DST / NTP | monotonic vs wall divergence | **intervals use Stopwatch/TickCount64; wall clock display-only** |
| 20 | Folder deleted/renamed server-side | LIST diff | reconcile; mark orphaned locals; handle NIL delimiter |
| 21 | Non-ASCII folder names (mUTF-7 vs UTF8=ACCEPT) | capability | MailKit handles mUTF-7; enable UTF8=ACCEPT when advertised |
| 22 | PERMANENTFLAGS without `\*` | open response | tags become local-only; do NOT loop pushing keywords |
| 23 | Read-only folder (EXAMINE) | `FolderAccess.ReadOnly` | never attempt flag writes |
| 24 | Message expunged between SEARCH and FETCH | FETCH empty/NO | skip gracefully |
| 25 | Flag conflict after offline period | MODSEQ diff both sides | **server wins on IMAP flags; local wins on tags** |
| 26 | APPEND to Sent races server auto-save | duplicate in Sent | dedupe by Message-ID; prefer the server copy |
| 27 | IDLE notification storm (bulk move of 10k) | flood of EXISTS/VANISHED | debounce/coalesce before resync |
| 28 | SMTP 451 / 421 / 5xx | response code | backoff retry / reconnect / permanent-fail surface |
| 29 | SMTPUTF8 absent but recipient is EAI | capability check | fail with a clear error; never silently mangle |
| 30 | Captive portal / DNS hijack → TLS failure | cert mismatch on known host | network backoff; do NOT count as auth failure |
| 31 | IPv6 advertised but broken | connect stalls | connect timeout + retry |
| 32 | 500k+ message account initial sync | high UIDNEXT | newest-first windowed backfill, resumable per-folder cursor |
| 33 | Disk full (SQLITE_FULL) | error code | pause sync, surface notification, never corrupt |
| 34 | User copies DB while running | n/a | document `VACUUM INTO`; provide a `backup` RPC |

## 14.6 Observability

With `EventSourceSupport=true`, `dotnet-counters` attaches. Build self-monitoring in: a `stats` RPC returning `Environment.WorkingSet64`, `GC.GetGCMemoryInfo()`, `GC.GetTotalAllocatedBytes()`, handle count, open IMAP connections, per-folder last-sync MODSEQ, WAL size, DB size; a `health` RPC returning per-account connection + auth state. Log a compact metrics line periodically to `sync_log`. **Do not pull in the OpenTelemetry SDK** for a single-user desktop daemon.

## 14.7 Explicitly NOT handled in v0.1
POP3-only servers · IMAP LOGIN-REFERRALS / MAILBOX-REFERRALS · MULTIAPPEND, BINARYMIME/CHUNKING upload optimizations · server-side SORT/THREAD (we thread client-side) · S/MIME and PGP crypto (parse without crashing, no decrypt/verify) · ManageSieve / server-side filters · JMAP (Graph is the v0.2 non-IMAP path).
