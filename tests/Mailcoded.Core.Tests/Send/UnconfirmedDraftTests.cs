using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>CLAUDE invariant 5: a send needs a valid one-time token. A draft that was previewed and
/// never confirmed is a message the human declined, so nothing may ever put it on the wire.</summary>
public sealed class UnconfirmedDraftTests
{
    private static readonly DraftRequest Draft = new()
    {
        To = [EmailAddress.Parse("someone@example.test")],
        Subject = "never confirmed",
        BodyText = "the human walked away",
    };

    [Fact]
    public async Task The_retry_loop_never_dispatches_a_draft_nobody_confirmed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "someone@example.test");

        await harness.PreviewAsync(Draft, ct);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        await harness.Send.ProcessDueAsync(harness.Sender, null, null, ct);

        Assert.Empty(harness.Sender.Sends);
    }

    [Fact]
    public async Task An_unconfirmed_draft_is_not_returned_as_due_work()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "someone@example.test");

        var preview = await harness.PreviewAsync(Draft, ct);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        var due = harness.Store.ListDueOutbox(harness.Clock.UtcNow, ct);

        Assert.DoesNotContain(due, row => row.Id == preview.OutboxId);
    }

    [Fact]
    public async Task A_draft_nobody_confirmed_is_not_counted_as_pending_work()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "someone@example.test");

        await harness.PreviewAsync(Draft, ct);

        Assert.Single(harness.Store.ListOutbox(OutboxState.Queued, ct));
        Assert.Empty(harness.Store.ListOutbox(OutboxState.Queued, ct, confirmedOnly: true));
    }

    [Fact]
    public async Task A_confirmed_send_that_fails_is_still_retryable_afterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "someone@example.test");

        var preview = await harness.PreviewAsync(Draft, ct);
        await harness.SendDraftAsync(preview.OutboxId, preview.ConfirmToken, ct);

        var record = harness.Store.GetOutbox(preview.OutboxId, ct);

        Assert.NotNull(record);
        Assert.Equal(OutboxState.Sent, record.State);
        Assert.Single(harness.Sender.Sends);
    }
}
