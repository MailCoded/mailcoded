using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>Recipients are learned from outgoing mail only: a send and its synced Sent copy count once, mail another client left in Sent counts, and received mail never does.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.Correspondents)]
public sealed class CorrespondentTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public CorrespondentTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task A_send_and_its_synced_copy_count_once_and_received_mail_teaches_nobody()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("correspondents");
        var secrets = new InMemorySecretStore();
        await using var harness = MailHarness.Open(workspace, secrets, _stack);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        var mailbox = _stack.Credentials.Mailbox;

        var subject = "mailcoded correspondents " + Guid.NewGuid().ToString("N");
        var preview = await harness.Send.PreviewAsync(
            CallerContext.Rpc,
            accountId,
            new DraftRequest
            {
                To = [EmailAddress.Parse("downstream@sink.test")],
                Subject = subject,
                BodyText = "Sent through mailcoded.",
            },
            ct);

        await using (var sender = await harness.ConnectSenderAsync(accountId, ct))
        {
            var sent = await harness.Send.SendAsync(CallerContext.Rpc, sender, provider, preview.OutboxId, preview.ConfirmToken, null, ct);
            Assert.Equal(OutboxState.Sent, sent.State);
        }

        Assert.Equal(1, Assert.Single(harness.Store.ListCorrespondents(accountId, ct)).Count);

        var otherClient = MessageBuilder.Build(new DraftSpec
        {
            From = EmailAddress.Parse(mailbox),
            To = [EmailAddress.Parse("pat@sink.test")],
            Subject = "From another client " + Guid.NewGuid().ToString("N"),
            BodyText = "Appended to Sent by something else.",
            MessageId = MessageId.Parse(Guid.NewGuid().ToString("N") + "@client.test"),
            DateUtc = SeedMail.Epoch,
        }, ct);
        await _stack.AppendAsync("Sent", [otherClient], ct);

        await _stack.AppendAsync("INBOX", [SeedMail.Build(mailbox, "Received " + subject, "Not outgoing.", 7)], ct);

        await harness.Sync.SyncAccountAsync(provider, accountId, null, ct);

        var learned = harness.Store.ListCorrespondents(accountId, ct).ToDictionary(c => c.Address);
        Assert.Equal(["downstream@sink.test", "pat@sink.test"], learned.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, learned["downstream@sink.test"].Count);
        Assert.Equal(1, learned["pat@sink.test"].Count);
        Assert.DoesNotContain("sender7@remote.test", learned.Keys);

        await harness.Sync.SyncAccountAsync(provider, accountId, null, ct);
        Assert.Equal(1, harness.Store.ListCorrespondents(accountId, ct).Single(c => c.Address == "pat@sink.test").Count);
    }
}
