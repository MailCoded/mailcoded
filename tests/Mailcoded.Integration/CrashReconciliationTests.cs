using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>RELIABILITY §14.4: a crash between SMTP 250 and the DB commit must never double-send.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.CrashRecovery)]
public sealed class CrashReconciliationTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public CrashReconciliationTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task A_crash_after_smtp_accepted_reconciles_from_sent_instead_of_resending()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("crash-sent");
        var secrets = new InMemorySecretStore();
        var subject = "mailcoded crash-sent " + Guid.NewGuid().ToString("N");

        long outboxId;
        AccountId accountId;

        await using (var harness = MailHarness.Open(workspace, secrets, _stack))
        {
            accountId = await PrepareAccountAsync(harness, ct);
            outboxId = await SimulateCrashAfterAcceptanceAsync(harness, accountId, subject, appendToSent: true, ct);
        }

        Assert.Equal(1, await _stack.Smtp.WaitForSubjectAsync(subject, 1, TimeSpan.FromSeconds(30), ct));

        await using (var restarted = MailHarness.Open(workspace, secrets, _stack))
        await using (var provider = await restarted.ConnectProviderAsync(accountId, ct))
        {
            var report = await restarted.Send.ReconcileStuckSendsAsync(restarted.Sync, provider, null, ct);

            Assert.Equal(1, report.MarkedSent);
            Assert.Equal(0, report.Requeued);

            var row = restarted.Store.GetOutbox(outboxId, ct);
            Assert.NotNull(row);
            Assert.Equal(OutboxState.Sent, row!.State);

            await using var sender = await restarted.ConnectSenderAsync(accountId, ct);
            var retried = await restarted.Send.ProcessDueAsync(sender, provider, null, ct);
            Assert.Empty(retried);
        }

        Assert.Equal(1, await _stack.Smtp.CountBySubjectAsync(subject, ct));
    }

    [Fact]
    public async Task A_stuck_send_the_server_cannot_confirm_is_investigated_never_resent()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("crash-stuck");
        var secrets = new InMemorySecretStore();
        var subject = "mailcoded crash-stuck " + Guid.NewGuid().ToString("N");

        long outboxId;
        AccountId accountId;

        await using (var harness = MailHarness.Open(workspace, secrets, _stack))
        {
            accountId = await PrepareAccountAsync(harness, ct);
            outboxId = await SimulateCrashAfterAcceptanceAsync(harness, accountId, subject, appendToSent: false, ct);
        }

        Assert.Equal(1, await _stack.Smtp.WaitForSubjectAsync(subject, 1, TimeSpan.FromSeconds(30), ct));

        await using (var restarted = MailHarness.Open(workspace, secrets, _stack))
        await using (var provider = await restarted.ConnectProviderAsync(accountId, ct))
        {
            var report = await restarted.Send.ReconcileStuckSendsAsync(restarted.Sync, provider, null, ct);

            Assert.Equal(1, report.NeedsInvestigation);
            Assert.Equal(0, report.Requeued);

            var row = restarted.Store.GetOutbox(outboxId, ct);
            Assert.NotNull(row);
            Assert.Equal(OutboxState.Sending, row!.State);

            await using var sender = await restarted.ConnectSenderAsync(accountId, ct);
            var retried = await restarted.Send.ProcessDueAsync(sender, provider, null, ct);
            Assert.Empty(retried);
        }

        Assert.Equal(1, await _stack.Smtp.CountBySubjectAsync(subject, ct));
    }

    private async Task<AccountId> PrepareAccountAsync(MailHarness harness, CancellationToken ct)
    {
        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        return accountId;
    }

    /// <summary>The bytes reach SMTP while the row stays <c>sending</c> — the state a process kill
    /// between the 250 and the commit leaves behind.</summary>
    private async Task<long> SimulateCrashAfterAcceptanceAsync(
        MailHarness harness,
        AccountId accountId,
        string subject,
        bool appendToSent,
        CancellationToken ct)
    {
        var preview = await harness.Send.PreviewAsync(
            CallerContext.Rpc,
            accountId,
            new DraftRequest
            {
                To = [EmailAddress.Parse("downstream@sink.test")],
                Subject = subject,
                BodyText = "Interrupted between the SMTP reply and the database commit.",
            },
            ct);

        var queued = harness.Store.GetOutbox(preview.OutboxId, ct)
            ?? throw new InvalidOperationException("The preview did not create an outbox row.");

        await harness.Store
            .SaveOutboxAsync(
                queued with { State = OutboxState.Sending, Attempts = 1, LastAttemptUtc = harness.Clock.UtcNow },
                ct)
            .ConfigureAwait(false);

        var envelope = queued.Envelope
            ?? throw new InvalidOperationException("The outbox row carries no addressed envelope.");

        await using (var sender = await harness.ConnectSenderAsync(accountId, ct))
        {
            await sender.SendAsync(queued.Raw, envelope.From, envelope.AllRecipients(), ct).ConfigureAwait(false);
        }

        if (appendToSent)
        {
            await using var provider = await harness.ConnectProviderAsync(accountId, ct);
            var sent = RequireSentFolder(harness, accountId, ct);
            await provider
                .AppendAsync(new FolderRef(sent.Id, sent.Path), queued.Raw, MessageFlags.None, harness.Clock.UtcNow, ct)
                .ConfigureAwait(false);
        }

        return preview.OutboxId;
    }

    private static FolderSummary RequireSentFolder(MailHarness harness, AccountId accountId, CancellationToken ct)
    {
        foreach (var folder in harness.Store.ListFolders(accountId, ct))
        {
            if (folder.Role == FolderRole.Sent) return folder;
        }

        throw new InvalidOperationException("The server exposes no Sent folder with SPECIAL-USE \\Sent.");
    }
}
