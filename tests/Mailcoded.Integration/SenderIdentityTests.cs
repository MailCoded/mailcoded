using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>A draft sent as a configured identity reaches the SMTP sink with that From and its signature; a stranger's From never queues.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.Identities)]
public sealed class SenderIdentityTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public SenderIdentityTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task A_configured_identity_is_sent_as_with_its_signature_and_a_stranger_is_refused()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("identity");
        var secrets = new InMemorySecretStore();
        await using var harness = MailHarness.Open(workspace, secrets, _stack);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);

        Assert.True(SenderIdentity.TryCreate("sales@alias.example.net", "Sales Desk", null, out var identity, out var error), error);
        await harness.Store.EditIdentitiesAsync(accountId, list => SenderIdentities.With(list, identity), ct);
        await harness.Store.EditSignaturesAsync(accountId, book => book.Set("work", "Integration Suite\nExample Pty"), ct);

        var subject = "mailcoded identity " + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<SenderNotAllowedException>(() => harness.Send.PreviewAsync(
            CallerContext.Rpc,
            accountId,
            new DraftRequest
            {
                From = EmailAddress.Parse("stranger@elsewhere.example.org"),
                To = [EmailAddress.Parse("downstream@sink.test")],
                Subject = subject,
                BodyText = "Must not queue.",
            },
            ct));

        Assert.Empty(harness.Send.ListOutbox(null, ct));

        var preview = await harness.Send.PreviewAsync(
            CallerContext.Rpc,
            accountId,
            new DraftRequest
            {
                From = EmailAddress.Parse("SALES@alias.example.net"),
                To = [EmailAddress.Parse("downstream@sink.test")],
                Subject = subject,
                BodyText = "Sent as an identity.",
                Signature = SignatureChoice.Named("work"),
            },
            ct);

        Assert.Equal("sales@alias.example.net", preview.From.Value);
        Assert.Equal("Sent as an identity.\n\n-- \nIntegration Suite\nExample Pty\n", preview.BodyPreview);

        await using var sender = await harness.ConnectSenderAsync(accountId, ct);
        var sent = await harness.Send.SendAsync(CallerContext.Rpc, sender, provider, preview.OutboxId, preview.ConfirmToken, null, ct);

        Assert.Equal(OutboxState.Sent, sent.State);
        Assert.True(sent.AppendedToSent, "The sent copy was not APPENDed to the Sent folder.");
        Assert.Equal(1, await _stack.Smtp.WaitForSubjectAsync(subject, 1, TimeSpan.FromSeconds(30), ct));

        var sentFolder = await _stack.ReadServerFolderAsync("Sent", ct);
        var copy = Assert.Single(sentFolder, e => string.Equals(e.Subject, subject, StringComparison.Ordinal));
        Assert.Contains("sales@alias.example.net", copy.From, StringComparison.Ordinal);
    }
}
