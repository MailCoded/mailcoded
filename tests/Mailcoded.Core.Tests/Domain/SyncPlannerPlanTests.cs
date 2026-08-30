using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>RELIABILITY §14.5 as a table. One row per case; no SQLite, no sockets, no clock.</summary>
public sealed class SyncPlannerPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed record PlanCase(
        string Name,
        string Rule,
        FolderState Local,
        ServerFolderInfo Server,
        ServerCaps Caps,
        DateTimeOffset NowUtc,
        Func<SyncPlan, bool> Matches);

    private static readonly PlanCase[] Matrix =
    [
        new PlanCase(
            "uidvalidity/first-sync",
            "§14.5 case 1: a folder with no stored UIDVALIDITY has never been synced, so there is nothing to diff "
            + "against — enumerate the folder from scratch.",
            Local(),
            Server(uidValidity: 42, total: 10),
            ServerCaps.None,
            default,
            p => p is SyncPlan.Invalidate),

        new PlanCase(
            "uidvalidity/changed-between-sessions",
            "§14.5 case 1: the epoch changed, so every stored UID now points at a different message. Discard them "
            + "and re-enumerate. This check runs BEFORE any delta path — QRESYNC and CONDSTORE cannot save it.",
            Local(uidValidity: 41, modSeq: 100, highestUid: 50),
            Server(uidValidity: 42, modSeq: 200, uidNext: 51),
            new ServerCaps { Qresync = true, Condstore = true },
            default,
            p => p is SyncPlan.Invalidate),

        new PlanCase(
            "qresync/available",
            "QRESYNC is the fast path: ask for everything since our MODSEQ and trust VANISHED for expunges.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50),
            Server(uidValidity: 42, modSeq: 200, uidNext: 51),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.QresyncDelta q
                 && q.Since == new ModSeq(100)
                 && q.UidValidity == new UidValidity(42)
                 && q.FromUid == new Uid(50)),

        new PlanCase(
            "qresync/broken-quirk-falls-back-to-condstore",
            "§14.5 cases 4 and 9: the server advertises QRESYNC but misbehaves (iCloud throws on open, others omit "
            + "VANISHED). A latched quirk always makes the planner MORE conservative, never faster.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Qresync = true, Condstore = true, Quirks = ServerQuirks.QresyncBroken },
            default,
            p => p is SyncPlan.CondstoreDelta c && c.Since == new ModSeq(100) && c.FromUid == new Uid(51)),

        new PlanCase(
            "qresync/unusable-without-a-local-modseq",
            "QRESYNC resumes from a MODSEQ we stored. Without one there is no 'since' to send, so fall back.",
            Local(uidValidity: 42, modSeq: 0, highestUid: 50),
            Server(uidValidity: 42, modSeq: 200, uidNext: 51),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.FullDiff),

        new PlanCase(
            "condstore/available",
            "CONDSTORE path: a CHANGEDSINCE flag fetch plus a UID SEARCH starting one past the highest UID we hold.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Condstore = true },
            default,
            p => p is SyncPlan.CondstoreDelta c && c.Since == new ModSeq(100) && c.FromUid == new Uid(51)),

        new PlanCase(
            "condstore/available-with-nothing-known-locally",
            "With no local UID the new-arrival search must start at UID 1, never at null — a null start would ask "
            + "the server for the whole folder as an unbounded range.",
            Local(uidValidity: 42, modSeq: 100),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Condstore = true },
            default,
            p => p is SyncPlan.CondstoreDelta c && c.FromUid == new Uid(1)),

        new PlanCase(
            "condstore/at-the-uid-ceiling",
            "A folder that has reached uint.MaxValue has no 'next' UID; the plan carries null rather than wrapping "
            + "around to 1 and re-fetching the entire folder.",
            Local(uidValidity: 42, modSeq: 100, highestUid: uint.MaxValue),
            Server(uidValidity: 42, modSeq: 200),
            new ServerCaps { Condstore = true },
            default,
            p => p is SyncPlan.CondstoreDelta c && c.FromUid is null),

        new PlanCase(
            "condstore/broken-quirk",
            "§14.5 case 4: MODSEQ was observed non-monotonic, so CONDSTORE deltas cannot be trusted. Full FLAGS + "
            + "SEARCH diff, carrying the UIDs we hold so the caller can compute the set difference.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50, knownUids: [new Uid(1), new Uid(2), new Uid(3)]),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Condstore = true, Quirks = ServerQuirks.CondstoreBroken },
            default,
            p => p is SyncPlan.FullDiff f && f.KnownUids.Count == 3),

        new PlanCase(
            "highestmodseq/zero-from-the-server",
            "§14.5 case 4: HIGHESTMODSEQ=0 means the server is not really tracking MODSEQ. Neither delta path is "
            + "safe, whatever it advertised in CAPABILITY.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50),
            Server(uidValidity: 42, modSeq: 0, uidNext: 51),
            new ServerCaps { Qresync = true, Condstore = true },
            default,
            p => p is SyncPlan.FullDiff),

        new PlanCase(
            "highestmodseq/zero-locally",
            "We never captured a MODSEQ for this folder, so there is no delta baseline to resume from.",
            Local(uidValidity: 42, modSeq: 0, highestUid: 50),
            Server(uidValidity: 42, modSeq: 200, uidNext: 51),
            new ServerCaps { Qresync = true, Condstore = true },
            default,
            p => p is SyncPlan.FullDiff),

        new PlanCase(
            "modseq/goes-backwards-under-qresync",
            "§14.5 case 4: a MODSEQ that went backwards proves the server's counter is not monotonic. The backwards "
            + "check outranks QRESYNC — the fast path would silently skip everything between the two values.",
            Local(uidValidity: 42, modSeq: 500, highestUid: 50, knownUids: [new Uid(50)]),
            Server(uidValidity: 42, modSeq: 400, uidNext: 51),
            new ServerCaps { Qresync = true, Condstore = true },
            default,
            p => p is SyncPlan.FullDiff f && f.KnownUids.Count == 1),

        new PlanCase(
            "modseq/goes-backwards-under-condstore",
            "§14.5 case 4: same rule with only CONDSTORE advertised.",
            Local(uidValidity: 42, modSeq: 500, highestUid: 50),
            Server(uidValidity: 42, modSeq: 400, uidNext: 51),
            new ServerCaps { Condstore = true },
            default,
            p => p is SyncPlan.FullDiff),

        new PlanCase(
            "uidnext/backwards-with-an-unchanged-modseq",
            "§14.5 case 3: a rewound UIDNEXT reads as 'no new mail' to the planner. UID reuse is caught downstream "
            + "by the sha256 rule in Store, which trusts the hash over the UID — never by this function.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 500),
            Server(uidValidity: 42, modSeq: 100, uidNext: 10),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.UpToDate),

        new PlanCase(
            "uidnext/backwards-with-an-advanced-modseq",
            "§14.5 case 3: a rewound UIDNEXT never suppresses a delta the MODSEQ says exists.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 500),
            Server(uidValidity: 42, modSeq: 200, uidNext: 10),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.QresyncDelta q && q.FromUid == new Uid(500)),

        new PlanCase(
            "condstore-cadence/first-diff-is-due",
            "CONDSTORE reports flag changes but never expunges, so a deletion stays invisible until a full diff "
            + "forces it into view. A folder that has never had one is due immediately.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50, lastFullDiff: null),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Condstore = true },
            Now,
            p => p is SyncPlan.FullDiff),

        new PlanCase(
            "condstore-cadence/not-yet-due",
            "Inside the 24 h window the cheap CONDSTORE delta is still correct for everything except expunges.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50, lastFullDiff: Now - TimeSpan.FromHours(23)),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Condstore = true },
            Now,
            p => p is SyncPlan.CondstoreDelta),

        new PlanCase(
            "condstore-cadence/due-exactly-at-the-interval",
            "The cadence boundary is inclusive: at exactly CondstoreFullDiffInterval the diff runs.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50, lastFullDiff: Now - SyncPlanner.CondstoreFullDiffInterval),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Condstore = true },
            Now,
            p => p is SyncPlan.FullDiff),

        new PlanCase(
            "condstore-cadence/inert-without-a-clock",
            "Plan() takes the instant as a parameter (CLAUDE invariant 13). A caller that passes none gets no "
            + "periodic diff at all — which is why the sync engine must always pass IClock.UtcNow.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50, lastFullDiff: null),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Condstore = true },
            default,
            p => p is SyncPlan.CondstoreDelta),

        new PlanCase(
            "condstore-cadence/does-not-apply-to-qresync",
            "QRESYNC reports VANISHED, so expunges are never hidden and the periodic full diff is pure waste.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 50, lastFullDiff: Now - TimeSpan.FromDays(100)),
            Server(uidValidity: 42, modSeq: 200, uidNext: 60),
            new ServerCaps { Qresync = true, Condstore = true },
            Now,
            p => p is SyncPlan.QresyncDelta),

        new PlanCase(
            "backfill/resume-mid-folder",
            "§14.5 case 32: an unfinished backfill is finished before anything else is considered. The window walks "
            + "newest-first, so the next slice ends one below the cursor.",
            Local(uidValidity: 42, modSeq: 100, backfillCursor: 10_000),
            Server(uidValidity: 42, modSeq: 200, uidNext: 20_000),
            new ServerCaps { Qresync = true, Condstore = true },
            Now,
            p => p is SyncPlan.Backfill b && b.FromUid == new Uid(5_000) && b.ToUid == new Uid(9_999)),

        new PlanCase(
            "backfill/resume-near-the-start",
            "§14.5 case 32: the last window clamps to UID 1 instead of underflowing.",
            Local(uidValidity: 42, modSeq: 100, backfillCursor: 3),
            Server(uidValidity: 42, modSeq: 200, uidNext: 20_000),
            new ServerCaps { Condstore = true },
            Now,
            p => p is SyncPlan.Backfill b && b.FromUid == new Uid(1) && b.ToUid == new Uid(2)),

        new PlanCase(
            "backfill/resume-at-uid-one",
            "A cursor of 1 still produces a legal range: Uid rejects 0, so the range degenerates to [1,1].",
            Local(uidValidity: 42, modSeq: 100, backfillCursor: 1),
            Server(uidValidity: 42, modSeq: 200, uidNext: 20_000),
            new ServerCaps { Condstore = true },
            Now,
            p => p is SyncPlan.Backfill b && b.FromUid == new Uid(1) && b.ToUid == new Uid(1)),

        new PlanCase(
            "backfill/outranks-a-broken-modseq",
            "Finishing the backfill comes before any delta or quirk handling; the cursor is the only state that "
            + "survives a restart mid-enumeration.",
            Local(uidValidity: 42, modSeq: 500, backfillCursor: 10_000),
            Server(uidValidity: 42, modSeq: 400, uidNext: 20_000),
            new ServerCaps { Qresync = true, Condstore = true },
            Now,
            p => p is SyncPlan.Backfill),

        new PlanCase(
            "no-work/qresync-matching-modseq-and-uidnext",
            "Nothing to do: same MODSEQ and UIDNEXT is exactly one past the highest UID we hold.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 100, count: 100),
            Server(uidValidity: 42, modSeq: 100, uidNext: 101, total: 100),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.UpToDate),

        new PlanCase(
            "no-work/condstore-matching-modseq-inside-the-cadence",
            "The CONDSTORE no-work case requires both a matching MODSEQ and no new arrivals, because CONDSTORE "
            + "alone never reveals an expunge.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 100, lastFullDiff: Now - TimeSpan.FromHours(1)),
            Server(uidValidity: 42, modSeq: 100, uidNext: 101, total: 100),
            new ServerCaps { Condstore = true },
            Now,
            p => p is SyncPlan.UpToDate),

        new PlanCase(
            "no-work/empty-folder",
            "With no local UID at all, 'no new messages' means the server reports an empty folder.",
            Local(uidValidity: 42, modSeq: 100),
            Server(uidValidity: 42, modSeq: 100, uidNext: 1, total: 0),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.UpToDate),

        new PlanCase(
            "no-work/withheld-when-uidnext-is-missing",
            "A server that reports no UIDNEXT gives us no way to prove there is no new mail, so we sync anyway.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 100),
            Server(uidValidity: 42, modSeq: 100),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.QresyncDelta),

        new PlanCase(
            "no-work/withheld-when-new-mail-arrived",
            "A matching MODSEQ does not imply a matching UIDNEXT: new arrivals still need fetching.",
            Local(uidValidity: 42, modSeq: 100, highestUid: 100),
            Server(uidValidity: 42, modSeq: 100, uidNext: 150),
            new ServerCaps { Qresync = true },
            default,
            p => p is SyncPlan.QresyncDelta),

        new PlanCase(
            "degraded/no-extension-at-all",
            "§14.5 case 5: no usable extension leaves only a full UID-range diff. The caller logs degraded_sync.",
            Local(uidValidity: 42, highestUid: 100),
            Server(uidValidity: 42, uidNext: 101),
            ServerCaps.None,
            default,
            p => p is SyncPlan.FullDiff),
    ];

    public static TheoryData<string> MatrixRows
    {
        get
        {
            var rows = new TheoryData<string>();
            foreach (var row in Matrix) rows.Add(row.Name);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(MatrixRows))]
    public void Plan_follows_the_reliability_edge_case_matrix(string name)
    {
        var row = Matrix.Single(c => string.Equals(c.Name, name, StringComparison.Ordinal));

        var plan = SyncPlanner.Plan(row.Local, row.Server, row.Caps, row.NowUtc);

        Assert.True(
            row.Matches(plan),
            $"[{row.Name}] {row.Rule}{Environment.NewLine}Planner returned: {plan}");
    }

    [Fact]
    public void Every_matrix_row_has_a_unique_name()
    {
        var duplicates = Matrix.GroupBy(c => c.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            $"Duplicate matrix row names hide a case: {string.Join(", ", duplicates)}. Each row is looked up by "
            + "name, so a duplicate silently runs one case twice and skips the other.");
    }

    [Fact]
    public void The_matrix_covers_every_plan_shape()
    {
        Type[] shapes =
        [
            typeof(SyncPlan.Invalidate),
            typeof(SyncPlan.QresyncDelta),
            typeof(SyncPlan.CondstoreDelta),
            typeof(SyncPlan.FullDiff),
            typeof(SyncPlan.UpToDate),
            typeof(SyncPlan.Backfill),
        ];

        var produced = Matrix
            .Select(c => SyncPlanner.Plan(c.Local, c.Server, c.Caps, c.NowUtc).GetType())
            .Distinct()
            .ToArray();

        foreach (var shape in shapes)
        {
            Assert.True(
                produced.Contains(shape),
                $"No matrix row produces {shape.Name}. Every branch of the planner needs at least one row, or a "
                + "regression in that branch will not be caught here.");
        }
    }

    [Fact]
    public void PlanInitial_enumerates_a_folder_at_or_below_the_backfill_threshold()
    {
        var plan = SyncPlanner.PlanInitial(Server(uidValidity: 42, uidNext: 20_001, total: SyncPlanner.BackfillThreshold));

        Assert.True(
            plan is SyncPlan.Invalidate,
            "A first sync at or below BackfillThreshold is one straightforward enumeration; the resumable backfill "
            + $"machinery only pays for itself above {SyncPlanner.BackfillThreshold} messages. Got: {plan}");
    }

    [Fact]
    public void PlanInitial_backfills_a_huge_folder_newest_first()
    {
        var plan = SyncPlanner.PlanInitial(Server(uidValidity: 42, uidNext: 500_001, total: 500_000));

        var backfill = Assert.IsType<SyncPlan.Backfill>(plan);
        Assert.Equal(new Uid(500_001 - SyncPlanner.BackfillWindow), backfill.FromUid);
        Assert.Equal(new Uid(500_000), backfill.ToUid);
    }

    [Fact]
    public void PlanInitial_cannot_backfill_without_a_uidnext()
    {
        var plan = SyncPlanner.PlanInitial(Server(uidValidity: 42, uidNext: null, total: 500_000));

        Assert.True(
            plan is SyncPlan.Invalidate,
            "Backfill windows are computed from UIDNEXT. A server that withholds it leaves plain enumeration as the "
            + $"only correct plan. Got: {plan}");
    }

    [Fact]
    public void The_condstore_full_diff_interval_is_twenty_four_hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), SyncPlanner.CondstoreFullDiffInterval);
        Assert.Equal(20_000, SyncPlanner.BackfillThreshold);
        Assert.Equal(5_000, SyncPlanner.BackfillWindow);
    }

    [Fact]
    public void Plan_rejects_missing_inputs_rather_than_guessing()
    {
        Assert.Throws<ArgumentNullException>(() => SyncPlanner.Plan(null!, Server(), ServerCaps.None));
        Assert.Throws<ArgumentNullException>(() => SyncPlanner.Plan(Local(), null!, ServerCaps.None));
        Assert.Throws<ArgumentNullException>(() => SyncPlanner.Plan(Local(), Server(), null!));
        Assert.Throws<ArgumentNullException>(() => SyncPlanner.PlanInitial(null!));
    }

    [Fact]
    public void Plan_is_a_pure_function_of_its_inputs()
    {
        foreach (var row in Matrix)
        {
            var first = SyncPlanner.Plan(row.Local, row.Server, row.Caps, row.NowUtc);
            var second = SyncPlanner.Plan(row.Local, row.Server, row.Caps, row.NowUtc);

            Assert.True(
                first.GetType() == second.GetType()
                && string.Equals(first.ToString(), second.ToString(), StringComparison.Ordinal),
                $"[{row.Name}] Plan() returned different results for identical inputs. The planner is the functional "
                + $"core: any hidden state here makes a crashed sync unsafe to replay. {first} vs {second}");
        }
    }

    private static FolderState Local(
        uint uidValidity = 0,
        ulong modSeq = 0,
        uint? highestUid = null,
        int count = 0,
        uint? backfillCursor = null,
        DateTimeOffset? lastFullDiff = null,
        IReadOnlyList<Uid>? knownUids = null) =>
        new()
        {
            FolderId = new FolderId(7),
            AccountId = new AccountId(1),
            Path = FolderPath.Create("INBOX"),
            UidValidity = new UidValidity(uidValidity),
            HighestModSeq = new ModSeq(modSeq),
            HighestKnownUid = highestUid.HasValue ? new Uid(highestUid.Value) : null,
            KnownMessageCount = count,
            KnownUids = knownUids,
            BackfillCursor = backfillCursor.HasValue ? new Uid(backfillCursor.Value) : null,
            LastFullDiffUtc = lastFullDiff,
        };

    private static ServerFolderInfo Server(
        uint uidValidity = 42,
        ulong modSeq = 0,
        uint? uidNext = null,
        int total = 0) =>
        new()
        {
            Path = FolderPath.Create("INBOX"),
            UidValidity = new UidValidity(uidValidity),
            HighestModSeq = new ModSeq(modSeq),
            UidNext = uidNext.HasValue ? new Uid(uidNext.Value) : null,
            TotalCount = total,
        };
}
