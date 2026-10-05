# PERFORMANCE — SPEC §15: Data-layer performance at 500k+ messages

**Status:** [DECIDED]

## 15.1 The mapping layer is not where performance lives

On Dapper's own BenchmarkDotNet suite (.NET 8): hand-coded `SqlCommand` = 119.70 us / 7,584 B; Dapper `QueryFirstOrDefault<T>` = 133.73 us / 11,608 B (**+11.7%**); EF Core compiled = 265.45 us; EF Core tracking = 317.12 us. At 1M rows Dapper matches or beats a naive raw ADO loop.

**Verdict: Dapper is not a performance decision.** Row mapping is single-digit-percent noise next to SQL and index design. Use raw ADO.NET on the writer hot path (you already own the command lifecycle) and Dapper.AOT for read-mapping boilerplate if it grows past ~15-20 hand-written mappers.

**The fastest correct pattern:** long-lived connections; prepared `SqliteCommand` objects with parameters re-bound per execution; ordinal `GetXxx` reads; **synchronous** SQLite calls on a dedicated writer thread (async is fake for SQLite — the methods run synchronously and only add Task allocation); single-transaction batching.

Microsoft.Data.Sqlite specifics: native connection pooling is on by default since 6.0; prepared statements are per-connection and are not cached across command objects — hold long-lived commands. Never `Cache=Shared` with WAL.

## 15.2 Performance budget

| Operation | Target | Mechanism |
|---|---|---|
| Envelope page (50 rows @ 500k) | < 10 ms | keyset seek on a covering index; no OFFSET |
| FTS search @ 500k docs | < 100 ms p95 | contentless FTS5, `MATCH ... ORDER BY rank LIMIT 50`, snippet only on the visible page |
| Initial ingest | >= 2,000 env/sec (network permitting) | single-txn batches, prepared reuse, deferred index + FTS |
| Unread badge | < 5 ms | denormalized per-folder counter |
| Thread view | < 20 ms | `ROW_NUMBER()` over a thread index |
| Semantic search @ 500k vectors | < 100 ms p95 | int8 brute-force scan, no approximate index |
| Embedding one message | < 50 ms | hand-written encoder, background, never on the writer thread |

## 15.3 Schema: FTS5 tables and indexes

```sql
-- Primary search index: contentless, phrase-capable
CREATE VIRTUAL TABLE msg_fts USING fts5(
  subject, body_text, from_addr, to_addr,
  content='',
  tokenize='porter unicode61 remove_diacritics 2',
  detail='full',          -- keep phrase/NEAR; do NOT use detail=none here
  prefix='2 3'            -- search-as-you-type (validate the size cost in M-perf)
);

-- Secondary index for CJK substring search
CREATE VIRTUAL TABLE msg_fts_cjk USING fts5(
  subject, body_text,
  content='',
  tokenize='trigram'      -- NO detail= option: see below
);

-- Secondary indexes: create AFTER backfill
CREATE INDEX ix_msg_folder_date
  ON messages(folder_id, date_utc DESC, id DESC, subject, from_addr, flags);  -- covering, page-ordered
CREATE INDEX ix_msg_unread   ON messages(folder_id) WHERE (flags & 1) = 1;  -- partial: bit0 is Unread
CREATE INDEX ix_msg_thread   ON messages(thread_key, date_utc DESC, id);
-- ix_msg_folder_date is folder-first and cannot order across folders. Measured at 500k rows:
-- ORDER BY date_utc DESC, id DESC LIMIT 2000 is 189.5 ms without this and 0.5 ms with it (~7.2 MB).
CREATE INDEX ix_msg_date     ON messages(date_utc DESC, id DESC);
-- messages.UNIQUE (folder_id, uid) already indexes that pair; no standalone index.
-- messages.automated / messages.encrypted (nullable 0/1, migration 009) carry no index: is:automated and
-- is:encrypted filter the date-ordered scan the way has:attachment does, and a partial index would cost
-- space in proportion to how much mail is automated. Add one only against a measurement at 500k rows.
-- messages.dmarc (nullable text, migration 010) is likewise unindexed: is:verified filters the same scan.
-- messages.unsubscribe (nullable text, migration 011, at most 4,000 chars and NULL or empty for most mail) is
-- read only by id and is not searchable, so it has no index and no query path of its own.
-- Category Tags (auto.*) are ordinary rows in tags: one extra insert per categorised message, inside the
-- ingest transaction. tag:auto.* uses ix_tags_tag. No new column or index.
```

`msg_fts_cjk` must keep FTS5's **default `detail='full'`**. The trigram tokenizer compiles a
substring search into a phrase query, and FTS5 rejects a phrase query outright when
`detail` is `none` or `column` — every CJK search fails with
`fts5: phrase queries are not supported (detail!=full)`. Trigram also needs at least three
characters, which is exactly why 1–2 character CJK falls back to `LIKE`.

Route queries by script detection: Latin → `msg_fts` (BM25 ranking), CJK → `msg_fts_cjk`, sub-trigram CJK (1-2 chars) → `LIKE` fallback.

## 15.4 Query shapes

**Keyset pagination (never OFFSET beyond ~page 20).** OFFSET degrades linearly — measured 0.28 ms at offset 0 vs 138 ms at offset 999,990 on a 1M-row table; keyset stays ~0.9 ms at any depth.

```sql
-- first page
SELECT id, subject, from_addr, date_utc, flags
FROM messages WHERE folder_id = ?
ORDER BY date_utc DESC, id DESC LIMIT 50;

-- next page (cursor = last row's date_utc, id)
SELECT id, subject, from_addr, date_utc, flags
FROM messages
WHERE folder_id = ? AND (date_utc, id) < (?, ?)
ORDER BY date_utc DESC, id DESC LIMIT 50;
```

**FTS + metadata filter:**

```sql
SELECT m.id, m.subject, m.from_addr, m.date_utc,
       snippet(msg_fts, 1, '[', ']', '...', 10) AS snip
FROM msg_fts
JOIN messages m ON m.id = msg_fts.rowid
WHERE msg_fts MATCH ? AND m.folder_id = ?
ORDER BY rank LIMIT 50;
```

Keep `LIMIT` small; call `snippet()`/`highlight()` only on the returned page, never across the full result set.

**Default Folder scope.** Unless the query names a Folder (or the request carries a `folderId`), every
search adds one uncorrelated predicate:

```sql
AND m.folder_id NOT IN (SELECT id FROM folders WHERE role IN ('trash', 'junk'))
```

SQLite evaluates the subquery once into a handful of ids and tests each candidate row against it, so
the plan still walks `ix_msg_date` for a date-ordered search and still seeks `(date_utc, id) < (?, ?)`
for a keyset page: no temp B-tree for the ORDER BY, no per-row probe of `folders`. A correlated
`NOT EXISTS` over `folders` gives the same plan but probes per row. Measured on a synthetic 500k-message
store (8% of rows in Trash/Junk, WAL off, one connection, python `sqlite3`): a metadata-only first page
and a deep keyset page stay at 0.1 ms, and a date-ordered FTS term matching 100k rows costs about
20-25 ms more than without the predicate, the price of testing each candidate row; a term matching
fewer rows costs proportionally less. Re-run `Mailcoded.Bench` before treating the FTS p95 as unchanged.
The semantic scan adds the same predicate, so hidden vectors never take a top-k slot.

**Thread view (latest per thread):**

```sql
SELECT id, thread_key, subject, from_addr, date_utc FROM (
  SELECT id, thread_key, subject, from_addr, date_utc,
         ROW_NUMBER() OVER (PARTITION BY thread_key ORDER BY date_utc DESC, id DESC) AS rn
  FROM messages WHERE folder_id = ?
) WHERE rn = 1
ORDER BY date_utc DESC LIMIT 50;
```

**Unread counts:** maintain `folders.unread_count` inside the writer transaction (or by trigger). Never `COUNT(*)` per sidebar render at 500k rows.

## 15.5 Bulk-ingest recipe (backfill mode)

A single enclosing transaction is the dominant factor — SQLite does tens of thousands of inserts/sec inside one transaction versus a few dozen transactions/sec without. Sub-batching *within* a transaction does not help; commit periodically only to cap WAL growth.

```
PRAGMA synchronous=OFF;         -- BACKFILL ONLY; restore NORMAL after
PRAGMA cache_size=-262144;      -- ~256 MB during backfill
PRAGMA temp_store=MEMORY;
-- DROP secondary indexes (keep PK)          [data-first is ~33% faster than index-first]
BEGIN;
  -- reuse ONE prepared INSERT; rebind params per row (ordinal)
  -- commit every ~5,000-10,000 rows to cap WAL, not for speed
COMMIT;
-- recreate secondary indexes
-- populate FTS in bulk (NOT via inline triggers during backfill):
INSERT INTO msg_fts(rowid, subject, body_text, from_addr, to_addr)
  SELECT id, subject, body_text, from_addr, to_addrs FROM messages WHERE ...;
INSERT INTO msg_fts(msg_fts) VALUES('optimize');
ANALYZE;
PRAGMA wal_checkpoint(TRUNCATE);
PRAGMA synchronous=NORMAL;      -- restore
```

**Exception to "DROP secondary indexes":** drop only indexes that serve *queries*. `ix_msg_message_id` stays, because the writer itself probes it once per `References`/`In-Reply-To` entry while resolving thread keys — dropping it makes every one of those lookups a full scan of the growing `messages` table, which is quadratic in the size of the backfill.

`synchronous=OFF` is safe against app crash but not OS/power crash. Acceptable **only** in the backfill window because IMAP is the source of truth and the DB can be rebuilt. Never in steady state.

`BEGIN CONCURRENT` and `wal2` remain branch-only in mainline SQLite as of 2026 — do not depend on them; the single-writer queue already sidesteps the need.

## 15.6 Where the real bottleneck is

Ordering for initial sync: **network >> parse > insert**. MimeKit parses ~1,000 messages in ~0.7 s in its own published benchmark (and is 25-75x faster than the common alternatives), and single-transaction SQLite ingests tens of thousands of envelope rows/sec. The gate is the IMAP FETCH — Gmail caps IMAP downloads at 2,500 MB/day and suspends on breach. Design the UX around network-bound initial sync (newest-first windowed backfill with a resumable cursor and visible progress, `notify.sync.progress`, docs/rpc.md §5), not around local throughput.

## 15.6a Vectors

Semantic search stores one int8 vector per message in `msg_vec`, keyed by `message_id` — the rowid
alias, so a candidate fetch is a point lookup. **Brute force is the design, not a placeholder.**

The numbers that chose this design came from an exploratory probe on the *development* machine
(NixOS on WSL2), not from `tests/Mailcoded.Bench` and not from reference hardware — treat them as
the reason for the decision, not as results: a full int8 scan over 500k synthetic vectors came in
around 11.8 ms on one thread at ~185 MiB, while `sqlite-vec` over the same set took 335-359 ms and
grew the database to ~750 MB. On that evidence an approximate index is premature at 500k rows. The
p95 gate above remains a target until the bench harness runs.

The encoder is written out in C# rather than taken as a dependency, for size: ONNX Runtime's
`libonnxruntime.so` is 28,985,152 bytes — 27.64 MiB. The size gate measures each shipped file on
its own, so that library does not eat into the daemon's 3.75 MiB of headroom: it **fails the 20 MB
gate outright**, at 1.4x the limit, before anything it is shipped beside is considered. The one
figure here that
*is* a measurement, because it is a byte count of a published binary rather than a timing, is the
encoder's cost in the AOT daemon: **158,000 bytes**, or 0.9% of the binary, against a 400 KB
sidecar trigger. It is 3.5x the 45,144 B the design estimated, because ILC deltas are not additive.

Vectors are derived data on the same footing as FTS: always rebuildable from `body_text`, droppable
at any time, never a durability domain. Comparing vectors from two models is silently meaningless,
so `model_id` filters every read and a fingerprint change invalidates rows by exclusion rather than
by a migration.

`System.Numerics.Vector<T>` throughout, never `Avx2.*` or `Fma.*`: those throw
`PlatformNotSupportedException` at ILC's default SSE2 baseline, while the portable API degrades to
whatever the machine actually has and needs no minimum-CPU raise.

## 15.7 Blob storage

SQLite is ~35% faster than the filesystem for small blobs; the documented crossover where external files win is roughly 250 KB-1 MB. The **512 KB externalization threshold** sits in that band and is well chosen. Validate `page_size=8192` (vs the 4096 default) for this blob-ish workload in M-perf rather than assuming it.

## 15.8 M-perf milestone

Build `tests/Mailcoded.Bench` (BenchmarkDotNet) plus a **synthetic-mailbox generator**: fixed seed, 500k envelopes, realistic subject/body length and vocabulary, ~10% CJK corpus, deterministic folder/thread/flag distribution. Name the exact reference machine in the methodology and publish the scripts — this doubles as the public benchmark that anchors the launch claims.

Benchmarks and gates:
- `EnvelopePage_Deep` (page 5,000): p95 < 10 ms
- `FtsSearch_CommonTerm`, `FtsSearch_Phrase`: p95 < 100 ms
- `Ingest_100k` (network mocked): >= 2,000 rows/sec sustained
- `UnreadBadge_AllFolders`: < 5 ms
- `ThreadView_TopFolder`: < 20 ms

Any regression >15% versus the committed baseline fails the build; re-baselining needs explicit PR approval. Express gates relative to the CI machine's own baseline where absolute ms would be hardware-dependent.

## 15.8a The 100k read-path test

`tests/Mailcoded.Core.Tests/Performance` holds an opt-in test in the normal unit suite. Without
`MAILCODED_TEST_PERF=1` it is reported as skipped, with the reason printed. With it, the test seeds
100,000 Messages through the bulk ingest path using the Bench corpus generator (linked source, fixed
seed), then prints n, p50, p95 and max for the first and a deep envelope page, FTS search, the unread
badge and the thread view. Seeding takes about two minutes.

Each ceiling is the §15.2 target, scaled to the corpus when the path is a scan (an index seek is not
scaled), times a safety factor of 10 for a shared machine, with a 10 ms floor. A path measured above
its scaled target carries a named extra multiple, so the test still trips on a regression without
pretending the target is met. Every line printed says whether the p95 is within its scaled target.
These are tripwires; the Bench gates in §15.8 stay the source of truth, and `docs/verification.md`
holds the one recorded run, labelled as a development-machine measurement.

```bash
MAILCODED_TEST_PERF=1 tests/Mailcoded.Core.Tests/bin/Debug/net10.0/Mailcoded.Core.Tests \
  --filter-class Mailcoded.Core.Tests.Performance.LargeCorpusPerformanceTests --output Detailed
```

## 15.9 Diagnostics when a budget is missed
1. `EXPLAIN QUERY PLAN` — confirm the query is index-only (no table access).
2. Confirm `ANALYZE` has populated `sqlite_stat1`.
3. Confirm the end-of-backfill FTS `optimize` ran.
4. Check `prefix='2 3'` index bloat before blaming FTS5 itself.
5. Check blobs aren't landing on the LOH (>= 85,000 bytes unpooled).
