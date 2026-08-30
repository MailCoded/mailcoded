# DEPENDENCIES — vetting rule, allowlist, and binary-size engineering

**Status:** [DECIDED]

## Dependency vetting rule

A new NuGet/npm dependency may be added ONLY if it satisfies ALL of:

1. **LICENSE** is MIT, Apache-2.0, or BSD-2/3-Clause.
   **BANNED:** RPL-1.5, PolyForm (any), BUSL, SSPL, Xceed Community, any "free under $X revenue" dual-commercial, any source-available/non-OSI licence.
2. **AOT-PROOF:** source-generator or reflection-free at runtime. No `System.Reflection.Emit`, no runtime IL gen, no open-generic DI registration that breaks trimming. Must publish clean under `PublishAot=true` with zero AOT warnings.
3. **SIZE BUDGET:** the net add keeps the per-RID binary under the CI gate. Measure the delta with sizoscope before merging.
4. **MAINTENANCE:** released within the last 12 months, or a stable feature-complete primitive. No archived/stalled repos.

## The 2025-2026 .NET OSS commercialization wave (why this rule exists)

| Library | Status | Licence now |
|---|---|---|
| **MediatR** | Commercial (Lucky Penny) from v13 | RPL-1.5 **or** paid |
| **AutoMapper** | Commercial (Lucky Penny) from v15 | RPL-1.5 **or** paid |
| **MassTransit** | v9 commercial; v8 stays Apache-2.0 | mixed |
| **FluentAssertions** | v8 relicensed via Xceed; paid for commercial use | Xceed Community |
| **EPPlus** | Commercial since v5 | PolyForm + paid |
| Wolverine / Marten | **stayed MIT** (open-core) | MIT |
| Brighter, FluentValidation, Polly, Serilog | still free | MIT / Apache-2.0 / BSD |

RPL-1.5 is strong copyleft reaching network-deployed and internal use — not a permissive fallback. Treat MediatR and AutoMapper as **banned regardless of revenue**.

## Approved allowlist

| Package | Licence | Why |
|---|---|---|
| MailKit >= 4.17.0 | MIT | IMAP/SMTP. IMAP4rev1/rev2 + IDLE/CONDSTORE/QRESYNC/COMPRESS; documented XOAUTH2 + MSAL flows |
| MimeKit >= 4.15.1 (prefer MimeKitLite in the AOT daemon) | MIT | MIME parse/build. 4.15.1 is the security floor (quoted-string addr-spec fix). MimeKitLite is AOT-clean |
| Microsoft.Data.Sqlite | MIT | ADO.NET SQLite provider; pooling since 6.0 |
| SQLitePCLRaw.bundle_e_sqlite3 | Apache-2.0 | Native SQLite with FTS4/FTS5/JSON1/R*Tree on all platforms |
| System.Text.Encoding.CodePages | MIT | **Required** for legacy MIME charsets; register the provider at startup |
| Microsoft.Extensions.DependencyInjection | MIT | Constructor registration ONLY |
| Microsoft.Identity.Client (v0.2) | MIT | MSAL device-code / auth-code for Graph + Gmail |
| Microsoft.Graph (v0.2) | MIT | Graph mail provider |
| ModelContextProtocol (M5) | MIT | Official C# SDK; stdio; multi-TFM incl. net10.0 |
| **Riok.Mapperly** | Apache-2.0 | PRE-APPROVED mapping fallback (source-gen). Only if hand-mapping exceeds ~10 files |
| **Dapper.AOT** | Apache-2.0 | PRE-APPROVED data-access fallback (interceptors, ~0 runtime size). Only past ~15-20 hand-written mappers |
| NetArchTest.Rules (test-only) | MIT | Architecture tests |
| sizoscope (dev tool) | MIT | AOT size analysis; not shipped |

**Explicitly banned:** MediatR, AutoMapper, Mapster (stalled + AOT-hostile), **EF Core under AOT** (experimental in EF10 + size), FluentAssertions v8+, MassTransit v9, EPPlus v5+, UPX.

## EF Core: the verdict

Microsoft's own EF Core NativeAOT documentation states the feature is "highly experimental... not yet suited for production use" and recommends against deploying EF NativeAOT applications in production. It also requires a compiled model plus precompiled queries and still trips the interceptors experimental gate in real setups. Combined with its size and reflection weight, EF Core is disqualified for a size-sensitive local daemon. **Raw ADO.NET + hand-written SQL is correct here** — the SQL is hand-tuned (FTS5 MATCH, PRAGMA management, single-writer queue, `user_version` migrations) in ways no ORM models well.

Minimal row-mapper helper (AOT-safe, no reflection):

```csharp
internal static class Db
{
    public static List<T> Query<T>(SqliteConnection cn, string sql,
        Func<SqliteDataReader, T> map, Action<SqliteParameterCollection>? bind = null)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd.Parameters);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
```

Per-entity ordinal maps live next to the entity.

## Binary size

**Baselines per RID.** .NET 10 AOT hello-world ~1.1-1.5 MB. e_sqlite3 native ~1.3-1.5 MB (linux-x64/win-x64), ~2.3 MB (macOS universal). Sockets/TLS +2-4 MB. MimeKit/MailKit managed after trimming +3-6 MB (no published measurement — estimate). Realistic total **~9-16 MB uncompressed per RID**, Linux at the high end because of app-local ICU.

**Globalization.** `InvariantGlobalization=false` is **required** — MIME needs legacy charset decoding via `CodePagesEncodingProvider`. Windows and macOS use OS-provided ICU (no size hit). Linux has no base-OS ICU: ship **app-local ICU** so the daemon works on machines without `libicu`. Decode-correctness beats size for a mail daemon.

**Size knobs.**

| Knob | Decision |
|---|---|
| `OptimizationPreference=Size` | **Use** — the daemon is I/O-bound |
| `UseSystemResourceKeys=true` | **Use** — framework message text isn't user-facing |
| `IlcFoldIdenticalMethodBodies=true` | **Use** — safe win |
| `StripSymbols=true` | **Use** — ship symbols separately |
| `DebuggerSupport=false` | Use in Release |
| `StackTraceSupport` | **Keep `true`** — CONFLICTS with size; supportability of a weeks-long daemon wins |
| `EventSourceSupport` | **Keep `true`** — CONFLICTS with size; needed for dotnet-counters |
| `InvariantGlobalization` | **`false`** — required |
| `InvariantTimezone` | **`false`** — mail dates need TZ data |
| UPX | **Never** — Defender false positives on AOT binaries, breaks macOS notarization/signing |

**Shipping property block:**

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <TargetFramework>net10.0</TargetFramework>
  <InvariantGlobalization>false</InvariantGlobalization>
  <PredefinedCulturesOnly>true</PredefinedCulturesOnly>
  <InvariantTimezone>false</InvariantTimezone>

  <OptimizationPreference>Size</OptimizationPreference>
  <UseSystemResourceKeys>true</UseSystemResourceKeys>
  <IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>
  <StripSymbols>true</StripSymbols>
  <DebuggerSupport>false</DebuggerSupport>

  <StackTraceSupport>true</StackTraceSupport>     <!-- size cost accepted: supportability -->
  <EventSourceSupport>true</EventSourceSupport>   <!-- size cost accepted: diagnostics -->
  <MetricsSupport>true</MetricsSupport>

  <IlcGenerateMstatFile>true</IlcGenerateMstatFile>
  <IlcGenerateDgmlFile>true</IlcGenerateDgmlFile>
</PropertyGroup>
```

**CI size gate:** warn > 12 MB, fail > 20 MB uncompressed per RID; warn > 6 MB, fail > 10 MB compressed. Archive `.mstat` and publish a size-trend chart. Tighten after the first real measurement with sizoscope (`dotnet tool install sizoscope --global`; artifacts land in `obj/Release/net10.0/<rid>/native/`).

**Measured, 2026-08-30, linux-x64 Release AOT:** `mailcoded-daemon` is **13.3 MB** — over the
12 MB warning line, under the 20 MB gate — with **zero** IL2xxx/IL3xxx trim or AOT warnings.

**Correction: it is not literally one file.** The AOT output is the executable *plus*
`libe_sqlite3.so` (1.5 MB), and the daemon cannot start without it. The `SQLite` native package
ships a static `e_sqlite3.a` for the iOS RIDs only; every desktop RID ships the shared library
alone, so there is nothing to statically link against. Ship the pair. This costs nothing in
practice — the GitHub Release archive and the platform-specific VSIX are both directories — but
"single binary" is the wrong phrase for it and should not appear in user-facing copy.

**Deployment model.** Framework-dependent is disqualified — a VS Code extension cannot assume .NET 10 on the user's machine, and download-on-first-run trades a one-time size saving for a recurring reliability liability. Native AOT (~9-16 MB, fastest start, lowest memory) is the right call.

**VSIX delivery:** ship **platform-specific VSIX targets** (`win32-x64`, `darwin-arm64`, `darwin-x64`, `linux-x64`) so each user downloads only their RID's daemon. Never one fat multi-RID VSIX (~40-65 MB and wasteful). Code-sign (Authenticode / Azure Trusted Signing on Windows, Apple notarization on macOS) — zipped AOT binaries trip Defender heuristics even without UPX.
