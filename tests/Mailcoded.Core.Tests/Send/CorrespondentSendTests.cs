using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Correspondents;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

public sealed class CorrespondentSendTests
{
    private static readonly SendOptions NoAppend = new() { AppendToSent = false };

    private sealed class FlakySender : IMailSender
    {
        private int _failures;

        public FlakySender(int failures) => _failures = failures;

        public int Attempts { get; private set; }

        public long? MaxMessageSize => null;

        public bool SupportsSmtpUtf8 => true;

        public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => Task.CompletedTask;

        public Task<string> SendAsync(byte[] raw, EmailAddress from, IReadOnlyList<EmailAddress> recipients, CancellationToken ct)
        {
            Attempts++;
            if (_failures-- > 0) throw SmtpDeliveryException.Create(451, "4.7.1 try again later", "4.7.1");
            return Task.FromResult("250 2.0.0 Ok: queued");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DraftRequest Draft(string[] to, string[]? cc = null, string[]? bcc = null) => new()
    {
        To = to.Select(EmailAddress.Parse).ToArray(),
        Cc = (cc ?? []).Select(EmailAddress.Parse).ToArray(),
        Bcc = (bcc ?? []).Select(EmailAddress.Parse).ToArray(),
        Subject = "Hello",
        BodyText = "Hi.",
    };

    private static Dictionary<string, Correspondent> Learned(SendPathHarness harness, CancellationToken ct) =>
        harness.Store.ListCorrespondents(harness.AccountId, ct).ToDictionary(c => c.Address, StringComparer.Ordinal);

    [Fact]
    public async Task A_successful_send_teaches_its_To_and_Cc_but_not_its_Bcc()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");

        var preview = await harness.PreviewAsync(Draft(["ada@example.org"], ["cy@example.org"], ["spy@example.org"]), ct);
        Assert.Empty(Learned(harness, ct));

        await harness.SendDraftAsync(preview.OutboxId, preview.ConfirmToken, ct);

        Assert.Equal(["ada@example.org", "cy@example.org"], Learned(harness, ct).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_preview_nobody_confirmed_teaches_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");

        await harness.PreviewAsync(Draft(["ada@example.org"]), ct);

        Assert.Empty(Learned(harness, ct));
    }

    [Fact]
    public async Task A_refused_send_teaches_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");
        var preview = await harness.PreviewAsync(Draft(["ada@example.org"]), ct);

        await Assert.ThrowsAsync<ConfirmRequiredException>(
            () => harness.SendDraftAsync(preview.OutboxId, "not-the-token", ct));

        Assert.Empty(Learned(harness, ct));
    }

    [Fact]
    public async Task A_send_that_fails_then_succeeds_on_retry_is_counted_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");
        var preview = await harness.PreviewAsync(Draft(["ada@example.org"]), ct);
        var sender = new FlakySender(failures: 2);

        await Assert.ThrowsAsync<SmtpDeliveryException>(
            () => harness.Send.SendAsync(SendPathHarness.Caller, sender, null, preview.OutboxId, preview.ConfirmToken, NoAppend, ct));
        Assert.Empty(Learned(harness, ct));

        harness.Clock.Advance(TimeSpan.FromDays(2));
        var first = await harness.Send.ProcessDueAsync(sender, null, NoAppend, ct);
        Assert.NotEqual(OutboxState.Sent, Assert.Single(first).State);
        Assert.Empty(Learned(harness, ct));

        harness.Clock.Advance(TimeSpan.FromDays(2));
        var second = await harness.Send.ProcessDueAsync(sender, null, NoAppend, ct);
        Assert.Equal(OutboxState.Sent, Assert.Single(second).State);

        harness.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Empty(await harness.Send.ProcessDueAsync(sender, null, NoAppend, ct));

        Assert.Equal(3, sender.Attempts);
        Assert.Equal(1, Learned(harness, ct)["ada@example.org"].Count);
    }

    [Fact]
    public async Task Sending_again_after_it_was_sent_does_not_count_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");
        var preview = await harness.PreviewAsync(Draft(["ada@example.org"]), ct);

        await harness.SendDraftAsync(preview.OutboxId, preview.ConfirmToken, ct);
        var again = await harness.PreviewAsync(Draft(["ada@example.org"]), ct);
        await harness.SendDraftAsync(again.OutboxId, again.ConfirmToken, ct);

        Assert.Equal(2, Learned(harness, ct)["ada@example.org"].Count);
    }

    [Fact]
    public async Task A_row_stuck_in_sending_that_reconciliation_finds_in_Sent_is_counted_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");
        var sent = await StoreSeed.FolderAsync(harness.Store, harness.AccountId, "Sent", FolderRole.Sent, ct);

        var messageId = MessageId.Parse("crashed-1@example.com");
        await harness.Store.EnqueueOutboxAsync(
            new Mailcoded.Core.Store.OutboxRecord
            {
                AccountId = harness.AccountId,
                MessageId = messageId,
                State = OutboxState.Sending,
                Raw = [1],
                Attempts = 1,
                Envelope = new Mailcoded.Core.Store.OutboxEnvelope
                {
                    From = EmailAddress.Parse("tester@example.com"),
                    To = [EmailAddress.Parse("ada@example.org")],
                },
            },
            ct);
        await StoreSeed.MessageAsync(
            harness.Store, sent,
            StoreSeed.Envelope(1, from: "tester@example.com", to: "ada@example.org", messageId: "crashed-1@example.com"),
            ct);
        Assert.Empty(Learned(harness, ct));

        var first = await harness.Send.ReconcileStuckSendsAsync(null, null, null, ct);
        var second = await harness.Send.ReconcileStuckSendsAsync(null, null, null, ct);

        Assert.Equal(1, first.MarkedSent);
        Assert.Equal(0, second.MarkedSent);
        Assert.Equal(1, Learned(harness, ct)["ada@example.org"].Count);
    }

    [Fact]
    public async Task A_row_stuck_in_sending_that_is_left_for_investigation_teaches_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");
        await StoreSeed.FolderAsync(harness.Store, harness.AccountId, "Sent", FolderRole.Sent, ct);

        await harness.Store.EnqueueOutboxAsync(
            new Mailcoded.Core.Store.OutboxRecord
            {
                AccountId = harness.AccountId,
                MessageId = MessageId.Parse("crashed-2@example.com"),
                State = OutboxState.Sending,
                Raw = [1],
                Attempts = 1,
                Envelope = new Mailcoded.Core.Store.OutboxEnvelope
                {
                    From = EmailAddress.Parse("tester@example.com"),
                    To = [EmailAddress.Parse("ada@example.org")],
                },
            },
            ct);

        var report = await harness.Send.ReconcileStuckSendsAsync(null, null, null, ct);

        Assert.Equal(1, report.NeedsInvestigation);
        Assert.Empty(Learned(harness, ct));
    }

    [Fact]
    public async Task Writing_to_the_account_itself_is_never_learned()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org", "@example.com");
        var preview = await harness.PreviewAsync(Draft(["tester@example.com", "ada@example.org"]), ct);

        await harness.SendDraftAsync(preview.OutboxId, preview.ConfirmToken, ct);

        Assert.Equal(["ada@example.org"], Learned(harness, ct).Keys);
    }
}
