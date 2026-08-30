# mailcoded benchmarks (M-perf)

These numbers anchor the public launch claims, so the method has to be reproducible by a stranger.
Everything here follows `docs/PERFORMANCE.md` §15.8.

## What runs

| Benchmark | What it measures | Gate |
|---|---|---|
| `EnvelopePage_Deep` | one keyset-seek envelope page at page 5,000 (50 rows) | p95 < 10 ms |
| `FtsSearch_CommonTerm` | `MATCH invoice ORDER BY rank LIMIT 50` with snippets | p95 < 100 ms |
| `FtsSearch_Phrase` | the phrase `"quarterly revenue forecast"` | p95 < 100 ms |
| `FtsSearch_Cjk` | trigram route, 3-rune CJK term | tracked, not gated |
| `UnreadBadge_AllFolders` | denormalized per-folder unread counters | p95 < 5 ms |
| `ThreadView_TopFolder` | `ROW_NUMBER()` latest-per-thread page | p95 < 20 ms |
| `Ingest_100k` | 100,000 envelopes into a fresh store, network mocked | >= 2,000 rows/sec |

Every gate is also checked as a **relative** rule: more than **15%** slower than the committed
baseline p95 fails the build, whatever the absolute figure.

## Reference machine

**No reference machine has been baselined yet.** `baseline.json` ships with the absolute budgets
from §15.2 and `p95Ns: 0` for every benchmark, which the gate reports as `----` (not baselined)
rather than silently passing.

When the first baseline is recorded, replace this section with the exact machine — CPU model,
core count, RAM, storage (NVMe model matters more than the CPU for this workload), OS build and
.NET version — and the `machine.id` the gate compares against. The gate prints the id it computed
for the current run; paste that.

Absolute millisecond budgets are **hardware-dependent**. The gate enforces them only when the
current machine id equals the baseline machine id; on any other machine it prints them as
informational and enforces the 15% rule alone. The run output says which mode it is in.

## The corpus

A fixed-seed synthetic mailbox: 500,000 envelopes, ~10% CJK, eight folders with a fixed share
distribution, deterministic thread and flag distribution, subject and body lengths drawn from a
fixed vocabulary.

Determinism does not rest on `System.Random` — it is not stable across .NET versions. The generator
uses a hand-rolled splitmix64 (`Corpus/DeterministicRandom.cs`), so the same seed reproduces the
same record stream forever. `--fingerprint` prints a hash of that stream; `--generate --verify`
regenerates and compares.

```bash
# build (or reuse) the 500k corpus and prove the seed reproduces it
dotnet run -c Release --project tests/Mailcoded.Bench -- --generate --verify

# a 10k smoke corpus instead
dotnet run -c Release --project tests/Mailcoded.Bench -- --generate --size 10000

# digest only, no disk
dotnet run -c Release --project tests/Mailcoded.Bench -- --fingerprint
```

The corpus is cached under `$TMPDIR/mailcoded-bench-corpus/seed<N>-n<SIZE>-cjk10/` and reused when
the manifest's digest still matches. Override the location with `--corpus-dir` or
`MAILCODED_BENCH_CORPUS_DIR`; the seed and size also come from `MAILCODED_BENCH_SEED` and
`MAILCODED_BENCH_SIZE`.

Building the 500k corpus takes minutes, and body text dominates: the store's bulk-ingest window has
no public way to supply body text, so each body is written in its own transaction after the window
closes. Envelope ingest itself is one bulk window and is fast. Budget roughly 5–10 minutes for the
first 500k build; later runs reuse the cache.

## Running the suite

```bash
# corpus + all benchmarks + gates; exit code 1 on a regression
dotnet run -c Release --project tests/Mailcoded.Bench

# one benchmark
dotnet run -c Release --project tests/Mailcoded.Bench -- --filter '*FtsSearch*'

# gate an existing run again without re-measuring
dotnet run -c Release --project tests/Mailcoded.Bench -- --gate
```

Results land in `BenchmarkDotNet.Artifacts/`; the gate reads the `*-report-full.json` exports and
takes `Statistics.Percentiles.P95`. `Ingest_100k` runs as a short job (3 warmup + 3 measured
iterations, one invocation each), so its p95 is effectively its worst of three — treat it as a
floor check, not a distribution.

## Re-baselining

**Re-baselining needs explicit PR approval.** It moves the gate, which is the only thing standing
between a regression and the launch claims.

```bash
dotnet run -c Release --project tests/Mailcoded.Bench -- --update-baseline
```

That rewrites `tests/Mailcoded.Bench/baseline.json` from the last run, keeping the absolute budgets
and replacing `p95Ns`, `machine.id` and the corpus fingerprint. A PR doing this must:

1. run on the reference machine named above, idle, on AC power, with no other load;
2. state why the previous numbers no longer apply (new schema, new index, new SQLite version);
3. carry a human approval — never an automated one.

A build that is merely slower is a regression, not a new baseline.
