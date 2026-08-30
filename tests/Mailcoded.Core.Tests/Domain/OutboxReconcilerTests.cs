using Mailcoded.Core.Domain.Outbox;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>The crash-window decision table — the "never double-send" rule as pure logic.</summary>
public sealed class OutboxReconcilerTests
{
    [Theory]
    [InlineData(true, false, true, true, false, SendReconciliation.MarkSent)]
    [InlineData(true, false, false, true, false, SendReconciliation.MarkSent)]
    [InlineData(true, true, false, false, false, SendReconciliation.MarkSent)]
    [InlineData(false, true, false, false, false, SendReconciliation.MarkSent)]
    [InlineData(false, false, false, false, false, SendReconciliation.Resend)]
    [InlineData(false, false, true, false, false, SendReconciliation.Resend)]
    [InlineData(false, false, true, false, true, SendReconciliation.Resend)]
    [InlineData(true, false, false, false, false, SendReconciliation.Investigate)]
    [InlineData(true, false, false, false, true, SendReconciliation.Investigate)]
    [InlineData(true, false, true, false, true, SendReconciliation.Resend)]
    [InlineData(true, false, true, false, false, SendReconciliation.Investigate)]
    public void The_crash_window_decision_table(
        bool dispatchStarted,
        bool smtpAccepted,
        bool sentFolderSearched,
        bool foundInSent,
        bool serverAutoSavesToSent,
        SendReconciliation expected)
    {
        var input = Input(dispatchStarted, smtpAccepted, sentFolderSearched, foundInSent, serverAutoSavesToSent);

        var decision = OutboxReconciler.Decide(input);

        Assert.True(
            decision == expected,
            $"Expected {expected} but got {decision} for {Describe(input)}.{Environment.NewLine}"
            + "The rule: a copy in Sent or a recorded 2xx proves delivery (MarkSent). Bytes that never reached SMTP, "
            + "or absence from Sent on a server that saves the copy ITSELF, disprove it (Resend). Everything else is "
            + "unknown, and an unknown must never be resolved by guessing — it goes to a human (Investigate).");
    }

    [Fact]
    public void Resend_is_returned_only_when_delivery_is_positively_disproved()
    {
        for (var mask = 0; mask < 32; mask++)
        {
            var dispatchStarted = (mask & 1) != 0;
            var smtpAccepted = (mask & 2) != 0;
            var sentFolderSearched = (mask & 4) != 0;
            var foundInSent = (mask & 8) != 0;
            var autoSaves = (mask & 16) != 0;

            var input = Input(dispatchStarted, smtpAccepted, sentFolderSearched, foundInSent, autoSaves);
            var decision = OutboxReconciler.Decide(input);

            var disproved = !foundInSent
                && !smtpAccepted
                && (!dispatchStarted || (sentFolderSearched && autoSaves));

            Assert.True(
                (decision == SendReconciliation.Resend) == disproved,
                $"{Describe(input)} produced {decision}. A resend is only safe when the evidence PROVES the message "
                + "never left: nothing in Sent, no recorded 2xx, and either the bytes never reached SMTP or the "
                + "server would have filed its own copy. Any other resend risks a duplicate in someone's inbox.");
        }
    }

    [Fact]
    public void Evidence_of_delivery_always_wins()
    {
        for (var mask = 0; mask < 32; mask++)
        {
            var input = Input((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, (mask & 16) != 0);
            var decision = OutboxReconciler.Decide(input);

            Assert.True(
                (decision == SendReconciliation.MarkSent) == (input.FoundInSent || input.SmtpAccepted),
                $"{Describe(input)} produced {decision}. A message found in Sent, or one with a durably recorded "
                + "2xx, has already been delivered: the only correct action is to close the row.");
        }
    }

    [Fact]
    public void An_unsearchable_sent_folder_is_never_resolved_by_guessing()
    {
        var decision = OutboxReconciler.Decide(new OutboxReconcileInput
        {
            State = OutboxState.Sending,
            DispatchStarted = true,
            SmtpAccepted = false,
            SentFolderSearched = false,
            FoundInSent = false,
        });

        Assert.Equal(SendReconciliation.Investigate, decision);
    }

    [Fact]
    public void Absence_from_sent_disproves_delivery_only_on_a_server_that_files_its_own_copy()
    {
        var manualSave = OutboxReconciler.Decide(Input(true, false, true, false, serverAutoSavesToSent: false));
        var autoSave = OutboxReconciler.Decide(Input(true, false, true, false, serverAutoSavesToSent: true));

        Assert.Equal(SendReconciliation.Investigate, manualSave);
        Assert.Equal(SendReconciliation.Resend, autoSave);
    }

    [Fact]
    public void The_short_overload_defaults_to_the_conservative_reading()
    {
        Assert.Equal(SendReconciliation.MarkSent, OutboxReconciler.Decide(sentFolderSearched: true, foundInSent: true));
        Assert.Equal(SendReconciliation.Investigate, OutboxReconciler.Decide(sentFolderSearched: true, foundInSent: false));
        Assert.Equal(SendReconciliation.Investigate, OutboxReconciler.Decide(sentFolderSearched: false, foundInSent: false));
    }

    [Theory]
    [InlineData(OutboxState.Queued)]
    [InlineData(OutboxState.Sent)]
    [InlineData(OutboxState.Failed)]
    public void Reconciliation_applies_to_the_crash_window_only(OutboxState state)
    {
        var input = new OutboxReconcileInput { State = state };

        Assert.Throws<ArgumentException>(() => OutboxReconciler.Decide(input));
    }

    [Fact]
    public void Reconciliation_rejects_a_missing_input_rather_than_guessing()
    {
        Assert.Throws<ArgumentNullException>(() => OutboxReconciler.Decide(null!));
    }

    private static OutboxReconcileInput Input(
        bool dispatchStarted,
        bool smtpAccepted,
        bool sentFolderSearched,
        bool foundInSent,
        bool serverAutoSavesToSent) =>
        new()
        {
            State = OutboxState.Sending,
            DispatchStarted = dispatchStarted,
            SmtpAccepted = smtpAccepted,
            SentFolderSearched = sentFolderSearched,
            FoundInSent = foundInSent,
            ServerAutoSavesToSent = serverAutoSavesToSent,
        };

    private static string Describe(OutboxReconcileInput input) =>
        $"dispatchStarted={input.DispatchStarted} smtpAccepted={input.SmtpAccepted} "
        + $"sentFolderSearched={input.SentFolderSearched} foundInSent={input.FoundInSent} "
        + $"serverAutoSavesToSent={input.ServerAutoSavesToSent}";
}
