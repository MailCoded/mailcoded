using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Sync;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class ServerDraftPolicyTests
{
    private static ServerDraftPlanInput Ready() => new()
    {
        Connected = true,
        SupportsMove = true,
        AppendToSent = true,
        ServerSavesSent = false,
        SentFolderKnown = true,
        DraftsFolderKnown = true,
    };

    [Fact]
    public void A_connected_account_with_move_a_sent_copy_and_both_folders_moves_the_draft()
    {
        var plan = ServerDraftPolicy.Plan(Ready());

        Assert.True(plan.Move);
        Assert.Equal(ServerDraftReason.None, plan.Reason);
    }

    public static TheoryData<Func<ServerDraftPlanInput, ServerDraftPlanInput>, ServerDraftReason> EveryReasonToLeave() => new()
    {
        { i => i with { Connected = false }, ServerDraftReason.NotConnected },
        { i => i with { ServerSavesSent = true }, ServerDraftReason.ServerSavesSent },
        { i => i with { AppendToSent = false }, ServerDraftReason.SentCopyNotWanted },
        { i => i with { SentFolderKnown = false }, ServerDraftReason.NoSentFolder },
        { i => i with { DraftsFolderKnown = false }, ServerDraftReason.NoDraftsFolder },
        { i => i with { SupportsMove = false }, ServerDraftReason.NoMoveCapability },
    };

    [Theory]
    [MemberData(nameof(EveryReasonToLeave))]
    public void Anything_short_of_a_safe_move_leaves_the_draft_and_names_why(
        Func<ServerDraftPlanInput, ServerDraftPlanInput> change,
        ServerDraftReason expected)
    {
        var plan = ServerDraftPolicy.Plan(change(Ready()));

        Assert.False(plan.Move);
        Assert.Equal(expected, plan.Reason);
    }

    [Fact]
    public void No_server_advertising_no_move_is_ever_planned_a_move_whatever_else_is_true()
    {
        for (var bits = 0; bits < 64; bits++)
        {
            var input = new ServerDraftPlanInput
            {
                Connected = (bits & 1) != 0,
                AppendToSent = (bits & 2) != 0,
                ServerSavesSent = (bits & 4) != 0,
                SentFolderKnown = (bits & 8) != 0,
                DraftsFolderKnown = (bits & 16) != 0,
                SupportsMove = false,
            };

            Assert.False(ServerDraftPolicy.Plan(input).Move);
        }
    }

    [Fact]
    public void A_server_that_saves_sent_mail_itself_never_gets_the_draft_moved_into_sent()
    {
        var plan = ServerDraftPolicy.Plan(Ready() with { ServerSavesSent = true });

        Assert.False(plan.Move);
        Assert.Equal(ServerDraftReason.ServerSavesSent, plan.Reason);
    }

    [Fact]
    public void Plan_rejects_a_null_input() =>
        Assert.Throws<ArgumentNullException>(() => ServerDraftPolicy.Plan(null!));

    [Fact]
    public void A_completed_move_owes_no_further_sent_copy()
    {
        var settlement = ServerDraftPolicy.SettleMove(AtomicMoveStatus.Moved);

        Assert.Equal(ServerDraftState.Moved, settlement.State);
        Assert.False(settlement.SentCopyStillNeeded);
    }

    [Theory]
    [InlineData(AtomicMoveStatus.EpochChanged, ServerDraftReason.UidValidityChanged)]
    [InlineData(AtomicMoveStatus.SourceGone, ServerDraftReason.DraftGone)]
    [InlineData(AtomicMoveStatus.Unsupported, ServerDraftReason.NoMoveCapability)]
    public void A_move_that_did_not_happen_leaves_the_draft_and_still_owes_the_sent_copy(AtomicMoveStatus status, ServerDraftReason reason)
    {
        var settlement = ServerDraftPolicy.SettleMove(status);

        Assert.Equal(ServerDraftState.Left, settlement.State);
        Assert.Equal(reason, settlement.Reason);
        Assert.True(settlement.SentCopyStillNeeded);
    }

    [Fact]
    public void An_unrecognised_move_status_is_an_error_not_a_guess() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerDraftPolicy.SettleMove((AtomicMoveStatus)99));

    [Fact]
    public void A_refusal_changed_nothing_so_the_sent_copy_is_still_owed()
    {
        var settlement = ServerDraftPolicy.SettleFailure(outcomeUnknown: false);

        Assert.Equal(ServerDraftState.Left, settlement.State);
        Assert.Equal(ServerDraftReason.MoveRefused, settlement.Reason);
        Assert.True(settlement.SentCopyStillNeeded);
    }

    [Fact]
    public void A_dropped_connection_may_have_moved_it_so_no_second_copy_is_made()
    {
        var settlement = ServerDraftPolicy.SettleFailure(outcomeUnknown: true);

        Assert.Equal(ServerDraftState.Left, settlement.State);
        Assert.Equal(ServerDraftReason.MoveUnconfirmed, settlement.Reason);
        Assert.False(settlement.SentCopyStillNeeded);
    }

    [Fact]
    public void No_state_or_reason_a_policy_can_produce_is_a_removal()
    {
        var names = Enum.GetNames<ServerDraftState>().Concat(Enum.GetNames<ServerDraftReason>());

        Assert.DoesNotContain(names, static n =>
            n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Expunge", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Trash", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Purge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_state_and_reason_round_trips_through_its_wire_value()
    {
        foreach (var state in Enum.GetValues<ServerDraftState>())
        {
            Assert.True(ServerDraftWire.TryParseState(state.ToWireValue(), out var parsed));
            Assert.Equal(state, parsed);
        }

        foreach (var reason in Enum.GetValues<ServerDraftReason>())
            Assert.Equal(reason, ServerDraftWire.ParseReason(reason.ToWireValue()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SAVED")]
    [InlineData("saved ")]
    [InlineData("deleted")]
    [InlineData("moved; DROP TABLE outbox")]
    [InlineData("left\0")]
    public void A_stored_state_that_is_not_exactly_one_of_ours_is_not_a_state(string? stored)
    {
        Assert.False(ServerDraftWire.TryParseState(stored, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No-Move-Capability")]
    [InlineData("expunged")]
    [InlineData("../../etc/passwd")]
    public void A_stored_reason_that_is_not_exactly_one_of_ours_reads_as_none(string? stored) =>
        Assert.Equal(ServerDraftReason.None, ServerDraftWire.ParseReason(stored));

    [Fact]
    public void An_out_of_range_state_or_reason_has_no_wire_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((ServerDraftState)42).ToWireValue());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((ServerDraftReason)42).ToWireValue());
    }
}
