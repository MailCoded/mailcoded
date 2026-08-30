using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>The fold half of the functional core: ordering, replay safety, quirk latching, counters.</summary>
public sealed class SyncPlannerApplyTests
{
    [Fact]
    public void Apply_folds_a_batch_in_order_and_reports_counts()
    {
        var state = State();
        var response = ServerResponse.Of(
            Added(10, modSeq: 5),
            FlagsChanged(10, MessageFlags.Flagged, modSeq: 7),
            Expunged(3),
            Batch(modSeq: 9, highestUid: 12, count: 1));

        var result = SyncPlanner.Apply(state, response);

        Assert.Equal(new SyncCounts(Added: 1, Updated: 1, Expunged: 1), result.Counts);
        Assert.Equal(new ModSeq(9), result.Next.HighestModSeq);
        Assert.Equal(new Uid(12), result.Next.HighestKnownUid);
        Assert.False(result.RequiresReplan);
    }

    [Fact]
    public void Apply_takes_the_maximum_uid_and_modseq_regardless_of_arrival_order()
    {
        var ascending = SyncPlanner.Apply(State(), ServerResponse.Of(Added(1, 10), Added(9, 90), Added(5, 50)));
        var descending = SyncPlanner.Apply(State(), ServerResponse.Of(Added(9, 90), Added(5, 50), Added(1, 10)));

        Assert.Equal(new Uid(9), ascending.Next.HighestKnownUid);
        Assert.Equal(new ModSeq(90), ascending.Next.HighestModSeq);
        Assert.Equal(ascending.Next, descending.Next);
    }

    [Fact]
    public void Apply_never_lowers_the_high_water_marks()
    {
        var state = State(modSeq: 500, highestUid: 900);

        var result = SyncPlanner.Apply(state, ServerResponse.Of(Added(3, 4), Batch(modSeq: 5, highestUid: 6, count: 1)));

        Assert.Equal(new ModSeq(500), result.Next.HighestModSeq);
        Assert.Equal(new Uid(900), result.Next.HighestKnownUid);
    }

    [Fact]
    public void Apply_is_idempotent_when_the_same_batch_is_replayed_after_a_rolled_back_transaction()
    {
        var state = State(modSeq: 100, highestUid: 50, count: 10);
        var response = new ServerResponse
        {
            Events =
            [
                Added(60, 150),
                FlagsChanged(51, MessageFlags.Unread, 160),
                Expunged(20),
                Quirk(ServerQuirks.LowConnectionLimit),
            ],
            ReportedHighestModSeq = new ModSeq(170),
            NextBackfillCursor = new Uid(40),
            PermanentFlagsAllowCustomKeywords = false,
        };

        var first = SyncPlanner.Apply(state, response);
        var second = SyncPlanner.Apply(state, response);

        Assert.Equal(first.Next, second.Next);
        Assert.Equal(first.Counts, second.Counts);
        Assert.Equal(first.Quirks, second.Quirks);
        Assert.Equal(first.RequiresReplan, second.RequiresReplan);
    }

    [Fact]
    public void Replaying_a_committed_batch_is_safe_for_the_high_water_marks_but_not_for_the_counter()
    {
        var state = State(modSeq: 100, highestUid: 50, count: 10);
        var response = ServerResponse.Of(Added(60, 200), Added(55, 150));

        var once = SyncPlanner.Apply(state, response);
        var twice = SyncPlanner.Apply(once.Next, response);

        Assert.Equal(once.Next.HighestModSeq, twice.Next.HighestModSeq);
        Assert.Equal(once.Next.HighestKnownUid, twice.Next.HighestKnownUid);

        Assert.True(
            twice.Next.KnownMessageCount == once.Next.KnownMessageCount + 2,
            "KnownMessageCount is a running total, not a max, so it is only correct once per batch. That is exactly "
            + "why CLAUDE invariant 9 requires the events and the folded state to be written in ONE transaction: a "
            + "replay must start from the pre-batch state, never from the folded one.");
    }

    [Fact]
    public void A_uidvalidity_change_mid_batch_demands_a_replan()
    {
        var state = State(modSeq: 100, highestUid: 50, count: 10, backfillCursor: 40);
        var response = ServerResponse.Of(
            Added(60, 200),
            new SyncEvent.UidValidityChanged(new UidValidity(42), new UidValidity(43)),
            Added(61, 210));

        var result = SyncPlanner.Apply(state, response);

        Assert.True(
            result.RequiresReplan,
            "§14.5 case 2: everything after a UIDVALIDITY change belongs to a different mailbox generation. Folding "
            + "on would attach the new epoch's UIDs to the old one's rows.");
        Assert.Equal(new UidValidity(43), result.Next.UidValidity);
        Assert.Equal(ModSeq.Zero, result.Next.HighestModSeq);
        Assert.Null(result.Next.HighestKnownUid);
        Assert.Null(result.Next.BackfillCursor);
        Assert.Equal(default(SyncCounts), result.Counts);
    }

    [Fact]
    public void A_uidvalidity_change_discards_the_folds_that_preceded_it_but_keeps_the_quirks()
    {
        var state = State(modSeq: 100, highestUid: 50);
        var response = ServerResponse.Of(
            Quirk(ServerQuirks.QresyncBroken),
            Added(60, 200),
            new SyncEvent.UidValidityChanged(new UidValidity(42), new UidValidity(43)));

        var result = SyncPlanner.Apply(state, response);

        Assert.Equal(ServerQuirks.QresyncBroken, result.Quirks);
        Assert.Null(result.Next.HighestKnownUid);
        Assert.Equal(3, result.Events.Count);
    }

    [Fact]
    public void Apply_latches_every_quirk_the_provider_reported()
    {
        var response = ServerResponse.Of(
            Quirk(ServerQuirks.QresyncBroken),
            Quirk(ServerQuirks.RequiresId),
            Quirk(ServerQuirks.QresyncBroken));

        var result = SyncPlanner.Apply(State(), response);

        Assert.Equal(ServerQuirks.QresyncBroken | ServerQuirks.RequiresId, result.Quirks);
    }

    [Fact]
    public void A_regressing_reported_modseq_latches_the_condstore_quirk_and_does_not_move_the_watermark()
    {
        var state = State(modSeq: 500);
        var response = new ServerResponse { Events = [], ReportedHighestModSeq = new ModSeq(400) };

        var result = SyncPlanner.Apply(state, response);

        Assert.True(
            result.Quirks.HasFlag(ServerQuirks.CondstoreBroken),
            "§14.5 case 4: a MODSEQ that went backwards proves the server's counter is not monotonic. Latch the "
            + "quirk so the next Plan() drops to a full diff instead of trusting another delta.");
        Assert.Equal(new ModSeq(500), result.Next.HighestModSeq);
    }

    [Fact]
    public void An_unknown_reported_modseq_is_not_evidence_of_a_broken_server()
    {
        var state = State(modSeq: 500);
        var response = new ServerResponse { Events = [], ReportedHighestModSeq = ModSeq.Zero };

        var result = SyncPlanner.Apply(state, response);

        Assert.Equal(ServerQuirks.None, result.Quirks);
        Assert.Equal(new ModSeq(500), result.Next.HighestModSeq);
    }

    [Fact]
    public void An_advancing_reported_modseq_moves_the_watermark()
    {
        var result = SyncPlanner.Apply(
            State(modSeq: 500),
            new ServerResponse { Events = [], ReportedHighestModSeq = new ModSeq(600) });

        Assert.Equal(new ModSeq(600), result.Next.HighestModSeq);
        Assert.Equal(ServerQuirks.None, result.Quirks);
    }

    [Fact]
    public void The_message_count_can_never_go_negative()
    {
        var state = State(count: 2);
        var response = ServerResponse.Of(Expunged(1), Expunged(2), Expunged(3), Expunged(4), Expunged(5));

        var result = SyncPlanner.Apply(state, response);

        Assert.Equal(5, result.Counts.Expunged);
        Assert.True(
            result.Next.KnownMessageCount == 0,
            "A folder cannot hold a negative number of messages. Expunges can legitimately outnumber the rows we "
            + "know about after a missed batch, so the arithmetic clamps at zero instead of persisting nonsense.");
    }

    [Fact]
    public void The_counter_arithmetic_is_adds_minus_expunges()
    {
        var result = SyncPlanner.Apply(
            State(count: 10),
            ServerResponse.Of(Added(1), Added(2), Added(3), Expunged(9), FlagsChanged(4, MessageFlags.Unread, 1)));

        Assert.Equal(new SyncCounts(3, 1, 1), result.Counts);
        Assert.Equal(12, result.Next.KnownMessageCount);
    }

    [Fact]
    public void A_batch_that_says_nothing_about_the_backfill_leaves_the_cursor_where_it_was()
    {
        var state = State(modSeq: 100, highestUid: 50, backfillCursor: 5_000);

        var carried = SyncPlanner.Apply(state, new ServerResponse { Events = [], NextBackfillCursor = new Uid(4_000) });
        var silent = SyncPlanner.Apply(state, ServerResponse.Of());
        var finished = SyncPlanner.Apply(state, new ServerResponse { Events = [], BackfillComplete = true });

        Assert.Equal(new Uid(4_000), carried.Next.BackfillCursor);
        Assert.True(
            silent.Next.BackfillCursor == new Uid(5_000),
            "Silence is not completion. A batch that does not restate the cursor used to clear it, so the next "
            + "Plan() saw no backfill outstanding and declared a half-enumerated 500k mailbox fully synced.");
        Assert.True(
            finished.Next.BackfillCursor is null,
            "Only the provider explicitly declaring the backfill finished ends it.");
    }

    [Fact]
    public void A_half_finished_backfill_survives_a_cursor_less_batch_and_is_replanned()
    {
        var state = State(modSeq: 100, highestUid: 50, backfillCursor: 5_000);
        var server = new ServerFolderInfo
        {
            Path = FolderPath.Create("INBOX"),
            UidValidity = new UidValidity(42),
            HighestModSeq = new ModSeq(200),
            UidNext = new Uid(500_001),
            TotalCount = 500_000,
        };

        var next = SyncPlanner.Apply(state, ServerResponse.Of(Added(4_999, 150))).Next;

        Assert.IsType<SyncPlan.Backfill>(SyncPlanner.Plan(
            next,
            server,
            new ServerCaps { Qresync = true, Condstore = true },
            new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_keyword_capability_survives_a_batch_that_says_nothing_about_permanentflags()
    {
        var state = State(acceptsKeywords: false);

        var silent = SyncPlanner.Apply(state, new ServerResponse { Events = [], PermanentFlagsAllowCustomKeywords = null });
        var restated = SyncPlanner.Apply(state, new ServerResponse { Events = [], PermanentFlagsAllowCustomKeywords = true });

        Assert.False(silent.Next.ServerAcceptsCustomKeywords);
        Assert.True(restated.Next.ServerAcceptsCustomKeywords);
    }

    [Fact]
    public void A_permanentflags_response_without_a_star_turns_keywords_off()
    {
        var result = SyncPlanner.Apply(
            State(acceptsKeywords: true),
            new ServerResponse { Events = [], PermanentFlagsAllowCustomKeywords = false });

        Assert.False(
            result.Next.ServerAcceptsCustomKeywords,
            "§14.5 case 22: PERMANENTFLAGS without \\* means custom keywords do not persist. Latching this is what "
            + "stops the next sync from pushing the same keywords forever.");
    }

    [Fact]
    public void Apply_returns_the_events_it_was_given()
    {
        var response = ServerResponse.Of(Added(1), Added(2));

        var result = SyncPlanner.Apply(State(), response);

        Assert.Same(response.Events, result.Events);
    }

    [Fact]
    public void Apply_rejects_missing_inputs_rather_than_guessing()
    {
        Assert.Throws<ArgumentNullException>(() => SyncPlanner.Apply(null!, ServerResponse.Of()));
        Assert.Throws<ArgumentNullException>(() => SyncPlanner.Apply(State(), null!));
    }

    private static FolderState State(
        ulong modSeq = 0,
        uint? highestUid = null,
        int count = 0,
        uint? backfillCursor = null,
        bool acceptsKeywords = true) =>
        new()
        {
            FolderId = new FolderId(7),
            AccountId = new AccountId(1),
            Path = FolderPath.Create("INBOX"),
            UidValidity = new UidValidity(42),
            HighestModSeq = new ModSeq(modSeq),
            HighestKnownUid = highestUid.HasValue ? new Uid(highestUid.Value) : null,
            KnownMessageCount = count,
            BackfillCursor = backfillCursor.HasValue ? new Uid(backfillCursor.Value) : null,
            ServerAcceptsCustomKeywords = acceptsKeywords,
        };

    private static SyncEvent Added(uint uid, ulong modSeq = 0) =>
        new SyncEvent.EnvelopeAdded(new RemoteEnvelope
        {
            Uid = new Uid(uid),
            Flags = MessageFlags.None,
            ModSeq = new ModSeq(modSeq),
        });

    private static SyncEvent FlagsChanged(uint uid, MessageFlags flags, ulong modSeq) =>
        new SyncEvent.FlagsChanged(new Uid(uid), flags, [], new ModSeq(modSeq));

    private static SyncEvent Expunged(uint uid) => new SyncEvent.MessageExpunged(new Uid(uid));

    private static SyncEvent Quirk(ServerQuirks quirk) => new SyncEvent.QuirkDetected(quirk, "observed in a test");

    private static SyncEvent Batch(ulong modSeq, uint? highestUid, int count) =>
        new SyncEvent.BatchComplete(
            new ModSeq(modSeq),
            highestUid.HasValue ? new Uid(highestUid.Value) : null,
            count);
}
