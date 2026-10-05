using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>An opted-in draft appears in the server's Drafts Folder, and after a send it is moved into Sent with one MOVE, never copied and flagged for expunge.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.ServerDrafts)]
public sealed class ServerDraftTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public ServerDraftTests(MailStackFixture stack) => _stack = stack;

    private static DraftRequest Draft(string subject) => new()
    {
        To = [EmailAddress.Parse("downstream@sink.test")],
        Subject = subject,
        BodyText = "Saved to the server first.",
    };

    [Fact]
    public async Task A_saved_draft_is_visible_to_another_client_and_a_send_moves_it_into_sent()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("server-draft-move");
        var secrets = new InMemorySecretStore();
        await using var harness = MailHarness.Open(workspace, secrets, _stack);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        await harness.Store.SetSaveDraftsToServerAsync(accountId, true, ct);

        var subject = "mailcoded server draft " + Guid.NewGuid().ToString("N");
        var preview = await harness.Send.PreviewAsync(
            CallerContext.Rpc,
            accountId,
            Draft(subject),
            _ => Task.FromResult<Mailcoded.Core.Providers.IMailProvider?>(provider),
            ct);

        Assert.Equal(ServerDraftSave.Saved, preview.ServerDraft);

        var drafts = await _stack.ReadServerFolderAsync("Drafts", ct);
        var onServer = Assert.Single(drafts, e => string.Equals(e.Subject, subject, StringComparison.Ordinal));
        Assert.True(onServer.Flags.HasFlag(MessageFlags.Draft));
        Assert.False(onServer.Flags.HasFlag(MessageFlags.Unread));
        Assert.Equal(preview.MessageId.Value, onServer.MessageIdHeader?.Trim('<', '>'));

        var recorded = Require(harness.Store.GetOutbox(preview.OutboxId, ct)?.ServerDraft);
        Assert.Equal(ServerDraftState.Saved, recorded.State);
        Assert.Equal(onServer.Uid, recorded.Uid);

        await using var sender = await harness.ConnectSenderAsync(accountId, ct);
        var sent = await harness.Send.SendAsync(CallerContext.Rpc, sender, provider, preview.OutboxId, preview.ConfirmToken, null, ct);

        Assert.Equal(OutboxState.Sent, sent.State);
        Assert.Equal(ServerDraftState.Moved, sent.ServerDraft);
        Assert.False(sent.AppendedToSent);
        Assert.Equal(1, await _stack.Smtp.WaitForSubjectAsync(subject, 1, TimeSpan.FromSeconds(30), ct));

        Assert.DoesNotContain(await _stack.ReadServerFolderAsync("Drafts", ct), e => string.Equals(e.Subject, subject, StringComparison.Ordinal));

        var copy = Assert.Single(await _stack.ReadServerFolderAsync("Sent", ct), e => string.Equals(e.Subject, subject, StringComparison.Ordinal));
        Assert.False(copy.Flags.HasFlag(MessageFlags.Draft));
        Assert.False(copy.Flags.HasFlag(MessageFlags.Deleted));
        Assert.Equal(preview.MessageId.Value, copy.MessageIdHeader?.Trim('<', '>'));
    }

    [Fact]
    public async Task A_renumbered_drafts_folder_leaves_the_draft_and_the_sent_copy_is_appended_instead()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("server-draft-epoch");
        var secrets = new InMemorySecretStore();
        await using var harness = MailHarness.Open(workspace, secrets, _stack);

        var accountId = await harness.AddAccountAsync(ct);
        var subject = "mailcoded renumbered draft " + Guid.NewGuid().ToString("N");
        long outboxId;
        string token;

        await using (var provider = await harness.ConnectProviderAsync(accountId, ct))
        {
            await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
            await harness.Store.SetSaveDraftsToServerAsync(accountId, true, ct);

            var preview = await harness.Send.PreviewAsync(
                CallerContext.Rpc,
                accountId,
                Draft(subject),
                _ => Task.FromResult<Mailcoded.Core.Providers.IMailProvider?>(provider),
                ct);

            Assert.Equal(ServerDraftSave.Saved, preview.ServerDraft);
            outboxId = preview.OutboxId;
            token = preview.ConfirmToken;
        }

        await _stack.Imap.ForceUidValidityChangeAsync(ct);

        await using var after = await harness.ConnectProviderAsync(accountId, ct);
        await using var sender = await harness.ConnectSenderAsync(accountId, ct);
        var sent = await harness.Send.SendAsync(CallerContext.Rpc, sender, after, outboxId, token, null, ct);

        Assert.Equal(OutboxState.Sent, sent.State);
        Assert.Equal(ServerDraftState.Left, sent.ServerDraft);
        Assert.Equal(ServerDraftReason.UidValidityChanged, sent.ServerDraftReason);
        Assert.True(sent.AppendedToSent);

        Assert.Single(await _stack.ReadServerFolderAsync("Drafts", ct), e => string.Equals(e.Subject, subject, StringComparison.Ordinal));
        Assert.Single(await _stack.ReadServerFolderAsync("Sent", ct), e => string.Equals(e.Subject, subject, StringComparison.Ordinal));
    }

    private static T Require<T>(T? value) where T : class => value ?? throw new InvalidOperationException("Expected a value.");
}
