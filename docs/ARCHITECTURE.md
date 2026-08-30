# ARCHITECTURE — SPEC §12: Hexagonal-lite with a Functional-Core Sync Engine

**Status:** [DECIDED]

**Decision.** Keep the four-project layout. `Mailcoded.Core` is the hexagon; `Daemon`/`Cli`/`Mcp` are driving adapters; `ImapProvider`/`SqliteStore`/secret stores are driven adapters. Inside Core, adopt **selective tactical DDD** — value objects, one true aggregate (Outbox), and a **pure-function sync core** — and explicitly reject enterprise Clean Architecture ceremony (project-per-ring layering, mediator bus, repositories, mapping libraries).

**Why.** Complexity lives in three places: the sync state machine (QRESYNC/CONDSTORE ladder, UIDVALIDITY invalidation), tag↔flag conflict rules, and outbox idempotency. Those deserve real modelling. Everything else is I/O adaptation, and ceremony around it costs a solo maintainer velocity for nothing: one team member, one storage engine, one process. Native AOT independently disqualifies reflection-based glue; the 2025 commercialization wave (MediatR + AutoMapper → Lucky Penny, dual RPL-1.5/commercial) settles the rest.

## 12.1 Core internal structure

Folders, not projects. Do **not** split Core into Domain/Application/Infrastructure `.csproj` files.

```
Mailcoded.Core/
├── Domain/                 # references NOTHING beyond the BCL
│   ├── Primitives/         # value objects (12.2)
│   ├── Sync/               # FolderState, ServerCaps, SyncPlan, SyncEvent, SyncPlanner (pure)
│   ├── Outbox/             # OutboxMessage aggregate + state machine (12.3)
│   ├── Tags/               # tag<->flag mapping + conflict rules (pure)
│   └── Threading/          # IThreader + ReferencesThreader (pure)
├── Application/            # SyncEngine, SendService, SearchService, AccountService
├── Providers/              # IMailProvider port + ImapProvider (ALL MailKit confined here)
├── Parsing/                # MimeKit confined here: raw blob -> Envelope/BodyText/Attachment DTOs
├── Store/                  # SqliteStore adapter (ALL SQL confined here) + migrations
└── Secrets/                # ISecretStore port + per-OS adapters
```

**Dependency rules (architecture-test enforced):**
1. `Domain` references no other Core namespace and no NuGet package.
2. `Application` references `Domain` + ports (`IMailProvider`, `ISecretStore`, `IThreader`, `IClock`) — never MailKit/MimeKit/Sqlite types.
3. Adapters reference `Domain` + their one external library; never each other.
4. Only host composition roots construct concrete adapters. Hand-wired root (<=50 lines per host); `Microsoft.Extensions.DependencyInjection` allowed for constructor registration only — no assembly scanning, no open generics, no scopes, no Generic Host.
5. Raw RFC822 bytes and `MimeMessage` never leave `Parsing`/`Providers`.

## 12.2 Value objects

Plain `readonly record struct` — no Vogen/StronglyTypedId dependency. Validation in a `TryParse`/factory.

| Type | Wraps | Invariant it protects |
|---|---|---|
| `Uid` | `uint` | >0; prevents transposing UID with local row ids |
| `ModSeq` | `ulong` | monotonic comparisons only via typed operators |
| `UidValidity` | `uint` | equality-only semantics (never ordered) |
| `MessageId` | `string` | normalized RFC5322 `<...>`; outbox idempotency + Sent dedupe |
| `ThreadKey` | `string` | non-empty; derivation rules live in `Threading` |
| `EmailAddress` | `string` | RFC-valid, domain lowercased; the CR/LF-injection gate at compose |
| `Tag` | `string` | notmuch-safe charset; maps to IMAP keyword or is local-only |
| `FolderPath` | `string` | normalized delimiter; INBOX-only case-insensitivity |
| `AccountId`/`FolderId`/`LocalMessageId` | `long` | typed row ids; no accidental cross-table joins |

## 12.3 The one aggregate: `OutboxMessage`

States `Queued -> Sending -> Sent | Failed`. Invariants inside the aggregate: `MessageId` assigned at creation (never at send time); legal transitions only; retry count/budget and next-attempt time owned by the aggregate; SMTP response recorded on terminal transition. `Store` persists it; state changes happen **only** through aggregate methods; `Application` writes the `sync_log` entry after the transaction commits. No domain-event infrastructure — `sync_log` is the event record.

`Account -> Folder -> Message` is **not** an aggregate. It is relational sync state with server-owned identity: data + pure functions, not entities with behavior.

## 12.4 Functional core, imperative shell

```csharp
// Domain/Sync — zero I/O, zero mocks required to test
public static class SyncPlanner
{
    public static SyncPlan Plan(FolderState local, ServerFolderInfo server, ServerCaps caps) =>
        local.UidValidity != server.UidValidity          ? SyncPlan.Invalidate(server)
        : caps.Qresync && !caps.Quirks.QresyncBroken     ? SyncPlan.QresyncDelta(local.HighestModSeq)
        : caps.Condstore && server.HighestModSeq > ModSeq.Zero
                                                         ? SyncPlan.CondstoreDelta(local.HighestModSeq)
        :                                                  SyncPlan.FullDiff(local.KnownUids);

    public static (FolderState next, IReadOnlyList<SyncEvent> events)
        Apply(FolderState state, ServerResponse response) => /* pure */;
}
```

`SyncEngine` (Application) is the imperative shell: execute the plan against `IMailProvider`, feed responses through `Apply`, persist `(next, events)` in one Store transaction.

**Consequence:** every row of the RELIABILITY edge-case matrix — UIDVALIDITY flip, `HIGHESTMODSEQ=0`, missing `VANISHED` — becomes a table-driven unit test on pure functions. Model `SyncPlan`/`SyncEvent` as sealed record hierarchies with exhaustive `switch`; migrate to C# discriminated unions when the language ships them.

## 12.5 Error policy

No `Result<T>` library (ErrorOr/FluentResults/OneOf).
- **Domain:** pure functions return decision types for expected states; throwing is reserved for programmer error (broken invariants).
- **Adapters:** wrap external failures in `ProviderException`/`StoreException` carrying a category (`Network | Protocol | Auth | Busy | Full`) — categories drive the reconnect/backoff rules in RELIABILITY.
- **Daemon boundary:** one exception→JSON-RPC mapper producing the stable numeric error table (SPEC §5.6). The RPC error table *is* the result type at the process boundary.

## 12.6 Ubiquitous language

Normative glossary: **Account, Folder, Envelope, Message, Blob, Tag** (local, notmuch-style) vs **Flag** (server IMAP), **Plan, SyncEvent, Outbox, Quirk**. SPEC terms = C# type names = JSON-RPC names, exactly. Doubly load-bearing because an AI agent implements the spec: consistent vocabulary keeps generated code converging instead of inventing synonyms.

## 12.7 Enforcement

Architecture tests run on CoreCLR at test time (AOT-irrelevant), NetArchTest.Rules or ArchUnitNET:

```csharp
[Fact]
public void Domain_is_dependency_free() =>
    Types.InAssembly(typeof(SyncPlanner).Assembly)
        .That().ResideInNamespace("Mailcoded.Core.Domain")
        .ShouldNot().HaveDependencyOnAny(
            "MailKit", "MimeKit", "Microsoft.Data.Sqlite",
            "Mailcoded.Core.Providers", "Mailcoded.Core.Store", "Mailcoded.Core.Parsing")
        .GetResult().IsSuccessful.Should().BeTrue();
```

Add equivalent tests for rules 2–3 of §12.1.

## 12.8 When to revisit
- A second storage backend or a plugin system appears → promote Store to a real port with two implementations.
- **GraphProvider (v0.2) strains `IMailProvider`** — watch for IMAP concepts leaking (fake UIDs, synthesized MODSEQ). Early signal: if `GraphProvider` needs >2 no-op or synthesized members, split the port into capability interfaces (`ISyncSource`, `IFlagWriter`, `ISender`).
- Team grows past 1–2 people or Core passes ~25k LOC → reconsider project-level splitting.
- C# discriminated unions ship → migrate `SyncPlan`/`SyncEvent`/`SyncDecision`.
