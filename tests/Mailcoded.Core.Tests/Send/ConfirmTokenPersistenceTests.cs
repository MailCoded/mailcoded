using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>A second ConfirmTokenStore over the same store is what `mailcoded send-draft` is:
/// a fresh process with empty memory redeeming what `send-preview` minted before it exited.</summary>
public sealed class ConfirmTokenPersistenceTests
{
    [Fact]
    public async Task ATokenMintedByOneInstanceIsRedeemedOnceByASecondInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var preview = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        var draft = await SeedAsync(temp.Store, account, "one@example.com", ct);

        var grant = await preview.IssueAsync(draft.Id, draft.MessageId, draft.Digest, ct);

        var nextProcess = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        Assert.True(nextProcess.IsOutstanding(draft.Id), "the token must outlive the process that minted it");

        Assert.True(await nextProcess.TryConsumeAsync(grant.Token, draft.Id, draft.MessageId, draft.Digest, ct));
        Assert.False(nextProcess.IsOutstanding(draft.Id));

        Assert.False(
            await nextProcess.TryConsumeAsync(grant.Token, draft.Id, draft.MessageId, draft.Digest, ct),
            "a confirm token is single use");
    }

    [Fact]
    public async Task ATokenMintedForOneDraftCannotSendAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var tokens = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        var first = await SeedAsync(temp.Store, account, "first@example.com", ct);
        var second = await SeedAsync(temp.Store, account, "second@example.com", ct);

        var firstGrant = await tokens.IssueAsync(first.Id, first.MessageId, first.Digest, ct);
        var secondGrant = await tokens.IssueAsync(second.Id, second.MessageId, second.Digest, ct);

        var nextProcess = new ConfirmTokenStore(temp.Clock, store: temp.Store);

        Assert.False(await nextProcess.TryConsumeAsync(secondGrant.Token, first.Id, first.MessageId, first.Digest, ct));
        Assert.True(nextProcess.IsOutstanding(first.Id), "a rejected attempt must not burn the token");

        Assert.False(
            await nextProcess.TryConsumeAsync(firstGrant.Token, first.Id, first.MessageId, "a-different-digest", ct),
            "a draft that changed after the preview must not be sendable with the old token");

        Assert.True(await nextProcess.TryConsumeAsync(firstGrant.Token, first.Id, first.MessageId, first.Digest, ct));
    }

    [Fact]
    public async Task AnExpiredTokenIsRejectedByTheProcessThatMintedItAndByTheNextOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var tokens = new ConfirmTokenStore(temp.Clock, TimeSpan.FromMinutes(10), store: temp.Store);
        var draft = await SeedAsync(temp.Store, account, "stale@example.com", ct);
        var grant = await tokens.IssueAsync(draft.Id, draft.MessageId, draft.Digest, ct);

        temp.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await tokens.TryConsumeAsync(grant.Token, draft.Id, draft.MessageId, draft.Digest, ct));

        var nextProcess = new ConfirmTokenStore(temp.Clock, TimeSpan.FromMinutes(10), store: temp.Store);
        Assert.False(nextProcess.IsOutstanding(draft.Id));
        Assert.False(await nextProcess.TryConsumeAsync(grant.Token, draft.Id, draft.MessageId, draft.Digest, ct));
    }

    [Fact]
    public async Task TwoConcurrentRedemptionsOfTheSameTokenProduceExactlyOneSend()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var tokens = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        var draft = await SeedAsync(temp.Store, account, "racing@example.com", ct);
        var grant = await tokens.IssueAsync(draft.Id, draft.MessageId, draft.Digest, ct);

        var left = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        var right = new ConfirmTokenStore(temp.Clock, store: temp.Store);

        var outcomes = await Task.WhenAll(
            Task.Run(() => left.TryConsumeAsync(grant.Token, draft.Id, draft.MessageId, draft.Digest, ct), ct),
            Task.Run(() => right.TryConsumeAsync(grant.Token, draft.Id, draft.MessageId, draft.Digest, ct), ct));

        var redeemed = 0;
        foreach (var consumed in outcomes)
        {
            if (consumed) redeemed++;
        }

        Assert.Equal(1, redeemed);
    }

    [Fact]
    public async Task ARevokedTokenIsGoneFromTheStoreNotJustFromMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var tokens = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        var draft = await SeedAsync(temp.Store, account, "revoked@example.com", ct);
        var grant = await tokens.IssueAsync(draft.Id, draft.MessageId, draft.Digest, ct);

        await tokens.RevokeAsync(draft.Id, ct);

        var nextProcess = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        Assert.False(nextProcess.IsOutstanding(draft.Id));
        Assert.False(await nextProcess.TryConsumeAsync(grant.Token, draft.Id, draft.MessageId, draft.Digest, ct));
    }

    [Fact]
    public async Task TheTokenItselfIsNeverWrittenToTheDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var tokens = new ConfirmTokenStore(temp.Clock, store: temp.Store);
        var draft = await SeedAsync(temp.Store, account, "opaque@example.com", ct);
        var grant = await tokens.IssueAsync(draft.Id, draft.MessageId, draft.Digest, ct);

        temp.Store.Dispose();

        var bytes = await File.ReadAllBytesAsync(temp.Workspace.DatabasePath, ct);
        Assert.DoesNotContain(grant.Token, Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
    }

    private static async Task<SeededDraft> SeedAsync(
        SqliteStore store,
        AccountId account,
        string messageId,
        CancellationToken ct)
    {
        var raw = Encoding.ASCII.GetBytes($"From: alice@example.com\r\nSubject: {messageId}\r\n\r\nbody");
        var id = await store.EnqueueOutboxAsync(
            new OutboxRecord
            {
                AccountId = account,
                MessageId = MessageId.Parse(messageId),
                State = OutboxState.Queued,
                Raw = raw,
                CreatedUtc = StoreSeed.BaseDate,
            },
            ct);

        return new SeededDraft(id, MessageId.Parse(messageId), AuditText.Digest(raw));
    }

    private readonly record struct SeededDraft(long Id, MessageId MessageId, string Digest);
}
