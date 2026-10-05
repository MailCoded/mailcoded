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
| **Microsoft.Identity.Client** | MIT | **Shipped now**: MSAL device-code signs in Microsoft accounts, so the binaries reach `login.microsoftonline.com`. Also the v0.2 path for Graph + Gmail |
| Microsoft.Graph (v0.2) | MIT | Graph mail provider |
| **ModelContextProtocol** | MIT | **Shipped now** in `mailcoded-mcp`. Official C# SDK; stdio; multi-TFM incl. net10.0. Not trim/AOT annotated, which is why that host is framework-dependent |
| **Riok.Mapperly** | Apache-2.0 | PRE-APPROVED mapping fallback (source-gen). Only if hand-mapping exceeds ~10 files |
| **Dapper.AOT** | Apache-2.0 | PRE-APPROVED data-access fallback (interceptors, ~0 runtime size). Only past ~15-20 hand-written mappers |
| NetArchTest.Rules (test-only) | MIT | Architecture tests |
| sizoscope (dev tool) | MIT | AOT size analysis; not shipped |
| TypeScript (npm dev tool, `packages/protocol`) | Apache-2.0 | type-checks the generated `@mailcoded/protocol`; not shipped, not referenced by any .NET project |

**Explicitly banned:** MediatR, AutoMapper, Mapster (stalled + AOT-hostile), **EF Core under AOT** (experimental in EF10 + size), FluentAssertions v8+, MassTransit v9, EPPlus v5+, UPX.

## User-supplied artifacts

**Status:** [DECIDED]

The four rules above assume a NuGet package. A model file is not one: it ships with no binary, is
chosen by the user, and is read as data at runtime. It gets its own tier.

An artifact may be read only if all of:

1. **Licence** is OSI-approved and permits redistribution of the weights. Record it, and print it
   before any tooling offers to fetch the file.
2. **Provenance** is a pinned URL and a **SHA-256** recorded in the repository. A file whose digest
   does not match is not loaded — not warned about, not loaded.
3. **Shape is declared and checked**: dimension, pooling, quantization and tokenizer vocabulary. A
   file that disagrees with its declaration is rejected rather than reinterpreted, because comparing
   vectors from two models is silently meaningless rather than loudly wrong.
4. **Parsed defensively.** The header is bounded before anything is allocated, a tensor whose offset
   plus length escapes the file is rejected, nothing is allocated from an unvalidated size field,
   and total mapped bytes are capped. Weights are data, never code.

**No shipped binary may fetch one.** Nothing shipped opens a connection except to the user's own
mail servers, and to Microsoft's identity service when an account signs in with OAuth (see the MSAL
row above). `mailcoded-tui` reaches neither: it speaks only to `mailcoded-daemon` over stdio. No
shipped binary ever fetches a model. Only `scripts/install.sh` may offer to download an artifact,
only on an explicit answer, and never by default.

An absent artifact is an absent feature: the capability reports false and the code path is
unreachable, rather than present and failing.

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

**Baselines per RID.** .NET 10 AOT hello-world ~1.1-1.5 MB. e_sqlite3 native ~1.3-1.5 MB (linux-x64/win-x64), ~2.3 MB (macOS universal). Sockets/TLS +2-4 MB. MimeKit/MailKit managed after trimming +3-6 MB (no published measurement — estimate). Realistic total **~9-17 MB uncompressed per RID** (the 2026-10-06 daemon measured 17.47 MiB), Linux at the high end because of app-local ICU.

**Attachment text extraction (BCL only).** Reading a Word `.docx` adds no package: it uses
`System.IO.Compression.ZipArchive` and a scanner written for the one XML vocabulary involved
(`WordXmlTextScanner`). `System.Xml` was measured and rejected. Published as a Native AOT daemon with
the extraction code stubbed out the binary was 17,902,256 bytes; with the zip and scanner it is
18,270,648 bytes, so the whole `.docx` path costs 368,392 bytes. The same build with the scanner replaced
by a minimal `XmlReader` (DTD processing prohibited, no resolver) was 18,919,576 bytes, 648,928 bytes
more than the scanner, and still leaves DTD, entity and resolver machinery in the binary that this code
only needs to refuse. Neither variant produced a trimming or AOT warning. These are byte counts of
published daemons on the development machine at the commit they were measured; they are not the release
sizes quoted below. `InvariantGlobalization=false` is **required** — MIME needs legacy charset decoding via `CodePagesEncodingProvider`. Windows and macOS use OS-provided ICU (no size hit). Linux has no base-OS ICU: ship **app-local ICU** so the daemon works on machines without `libicu`. Decode-correctness beats size for a mail daemon.

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

**CI size gate:** applied to each shipped file, never to their sum — warn > 12 MB, fail > 20 MB uncompressed; warn > 6 MB, fail > 10 MB compressed. The total is reported, not gated. Archive `.mstat` and publish a size-trend chart. Tighten after the first real measurement with sizoscope (`dotnet tool install sizoscope --global`; artifacts land in `obj/Release/net10.0/<rid>/native/`).

**Measured 2026-10-06**, publishing exactly what `scripts/install.sh` ships and sizing it with
`scripts/size-gate.sh`.

*Native AOT, linux-x64* — `mailcoded-daemon` **17.47 MiB**, `mailcoded` (CLI) **16.69 MiB**,
`mailcoded-tui` **6.17 MiB**, plus the `libe_sqlite3.so` **1.40 MiB** each of them loads. All three
are under the 20 MB gate; the daemon and the CLI are over the 12 MB warning line and the TUI is
not. Gzipped they are 8.07 / 7.79 / 2.83 MiB, so the first two are also past the 6 MB compressed
warning — though `size-gate.sh` never says so for them, because its checks are ordered and the
uncompressed warning fires first. **Zero** IL2xxx/IL3xxx trim or AOT warnings. **Rule 3's headroom
is 2.53 MiB, on `mailcoded-daemon`** — the largest AOT binary, and so the one a new native
dependency has to fit beside.

*Framework-dependent* — `mailcoded-mcp` is not compiled AOT. Its project sets `PublishAot=false`
and disables the trim and AOT analyzers, because the MCP SDK is annotated for neither, so a "zero
trim warnings" claim about it would be vacuous rather than measured. It is published
`--self-contained false` and needs the .NET runtime present. `mailcoded-mcp` **13.14 MiB** in
total, nearly all of it managed assemblies behind a 0.10 MiB launcher.

Publish it **with `-r`** even though it is framework-dependent. Without one, the output carries a
`runtimes/` tree of `e_sqlite3` builds for all thirty-one RIDs the package knows — 51.42 MiB of
Android, iOS and wasm natives inside a linux-x64 artifact. The per-file gate cannot see that,
because no single file in the tree is large.

The AOT CLI was then exercised end to end: all 56 MIME fixtures import, and full-text, metadata,
`is:automated`, `is:encrypted`, `is:verified`, `auto.*` Tag and `attachment:` searches all return the
same results as the JIT build. MimeKit, SQLitePCLRaw and the `.docx` path (ZipArchive) survive
trimming intact.

**Microsoft.Identity.Client (MSAL) is AOT-clean, and costs about 3 MB.** Measured when it landed,
with the OAuth device-code path actually referenced: **zero** IL2xxx/IL3xxx warnings, and the
linux-x64 Release AOT CLI grew by roughly 3 MB. The absolute figures from that run are superseded
by the measurement above; the delta is what the decision rested on. If the daemon later
approaches the gate, the lever is to move OAuth behind a capability the MCP adapter does not carry,
not to drop the dependency — token refresh is not optional once an account uses OAuth.

**BouncyCastle: trimmed out of the AOT binaries, shipped by the framework-dependent one.**
CLAUDE.md invariant 2 says "no S/MIME / PGP / BouncyCastle anywhere", but MimeKit's `net10.0`
target declares `BouncyCastle.Cryptography` and `System.Security.Cryptography.Pkcs`, so both enter
the graph the moment MailKit is referenced. Measured from a real install: the AOT `mailcoded` and
`mailcoded-daemon` contain **zero** references to it — the trimmer removes it because no code path
reaches it. The framework-dependent `mailcoded-mcp` publish, which does not trim, **does** ship
`BouncyCastle.Cryptography.dll` and `System.Security.Cryptography.Pkcs.dll` next to it, and
`scripts/install.sh --no-aot` ships them for all three commands.

PGP and S/MIME are excluded for two reasons: they are out of scope for v0.x, and decrypted
content must never reach an agent or the store (`docs/AGENT-INTERFACE.md`, "Encrypted mail"). The
size gate is a separate, narrower reason why BouncyCastle in particular stays out: linking a crypto
library would break the 20 MiB budget. Decryption through an external `gpg` process would link no
library and would not touch the AOT build, so AOT is not what keeps it out.

Read the invariant as what it protects: no crypto *code path*, and none of it in the AOT binaries
that are the shipping artifact. Two consequences to keep in view — the MCP adapter's disk footprint
carries dead crypto, and it inherits that library's CVE surface even though nothing calls it. If
that becomes unacceptable, the fix is to make the MCP adapter AOT-clean, not to fork MimeKit.

**Correction: it is not literally one file.** The AOT output is the executable *plus*
`libe_sqlite3.so` (1.40 MiB), and the daemon cannot start without it. The `SQLite` native package
ships a static `e_sqlite3.a` for the iOS RIDs only; every desktop RID ships the shared library
alone, so there is nothing to statically link against. Ship the pair. This costs nothing in
practice — the GitHub Release archive and the platform-specific VSIX are both directories — but
"single binary" is the wrong phrase for it and should not appear in user-facing copy.

**Deployment model.** Native AOT (~9-17 MB, fastest start, lowest memory) for everything the extension drives — a VS Code extension cannot assume .NET 10 on the user's machine, and download-on-first-run trades a one-time size saving for a recurring reliability liability. The one exception is `mailcoded-mcp`, whose SDK is not AOT-annotated: it is framework-dependent and does require a .NET runtime, which is why nothing in the extension's path depends on it.

**VSIX delivery:** ship **platform-specific VSIX targets** (`win32-x64`, `darwin-arm64`, `darwin-x64`, `linux-x64`) so each user downloads only their RID's daemon. Never one fat multi-RID VSIX (~40-65 MB and wasteful). Code-sign (Authenticode / Azure Trusted Signing on Windows, Apple notarization on macOS) — zipped AOT binaries trip Defender heuristics even without UPX.
