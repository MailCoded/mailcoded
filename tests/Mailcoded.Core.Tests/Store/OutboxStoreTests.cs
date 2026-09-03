using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class OutboxStoreTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.FromUnixTimeMilliseconds(1_780_000_000_000);

    [Fact]
    public async Task QueuedSendRoundTripsIncludingTheBccEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var raw = "From: alice@example.com\r\nSubject: hi\r\n\r\nbody"u8.ToArray();
        var id = await temp.Store.EnqueueOutboxAsync(Record(account, raw), ct);

        var loaded = Require.Ref(temp.Store.GetOutbox(id, ct), "the outbox row");

        Assert.Equal(account, loaded.AccountId);
        Assert.Equal("outbox-1@example.com", loaded.MessageId.Value);
        Assert.Equal(OutboxState.Queued, loaded.State);
        Assert.Equal(raw, loaded.Raw);
        Assert.Equal(Created, loaded.CreatedUtc);
        Assert.Equal(5, Require.Value(loaded.MaxAttempts, "the retry budget"));
        Assert.False(loaded.PermanentlyFailed);

        var envelope = Require.Ref(loaded.Envelope, "the captured envelope");
        Assert.Equal("alice@example.com", envelope.From.Value);
        Assert.Equal(new[] { "bob@example.org" }, Addresses(envelope.To));
        Assert.Equal(new[] { "carol@example.net" }, Addresses(envelope.Cc));
        Assert.Equal(new[] { "dave@example.org", "erin@example.org" }, Addresses(envelope.Bcc));
        Assert.Equal(4, envelope.AllRecipients().Count);
    }

    [Fact]
    public async Task EnqueuingTheSameMessageIdTwiceReturnsTheFirstRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var first = await temp.Store.EnqueueOutboxAsync(Record(account, "one"u8.ToArray()), ct);
        var second = await temp.Store.EnqueueOutboxAsync(Record(account, "two"u8.ToArray()), ct);

        Assert.Equal(first, second);
        Assert.Single(temp.Store.ListOutbox(null, ct));
        Assert.Equal("one"u8.ToArray(), Require.Ref(temp.Store.GetOutbox(first, ct), "the outbox row").Raw);
    }

    [Fact]
    public async Task EnqueuingWithoutRawBytesIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        await Assert.ThrowsAsync<ArgumentException>(
            () => temp.Store.EnqueueOutboxAsync(Record(account, []), ct));
    }

    [Fact]
    public async Task PermanentFailureColumnsSurviveASave()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var id = await temp.Store.EnqueueOutboxAsync(Record(account, "raw"u8.ToArray()), ct);
        var attempted = Created.AddMinutes(3);

        await temp.Store.SaveOutboxAsync(
            Require.Ref(temp.Store.GetOutbox(id, ct), "the outbox row") with
            {
                State = OutboxState.Failed,
                Attempts = 3,
                PermanentlyFailed = true,
                SmtpResponse = "550 5.1.1 unknown recipient",
                EnhancedStatusCode = "5.1.1",
                LastAttemptUtc = attempted,
                NextAttemptUtc = null,
            },
            ct);

        var loaded = Require.Ref(temp.Store.GetOutbox(id, ct), "the failed row");
        Assert.Equal(OutboxState.Failed, loaded.State);
        Assert.Equal(3, loaded.Attempts);
        Assert.True(loaded.PermanentlyFailed);
        Assert.Equal("550 5.1.1 unknown recipient", loaded.SmtpResponse);
        Assert.Equal("5.1.1", loaded.EnhancedStatusCode);
        Assert.Equal(attempted, Require.Value(loaded.LastAttemptUtc, "the last attempt"));
        Assert.Null(loaded.NextAttemptUtc);
    }

    [Fact]
    public async Task SavingARowWithoutAnEnvelopeNeverErasesTheStoredBcc()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var id = await temp.Store.EnqueueOutboxAsync(Record(account, "raw"u8.ToArray()), ct);

        var listed = Assert.Single(temp.Store.ListOutbox(null, ct));
        Assert.Empty(listed.Raw);

        await temp.Store.SaveOutboxAsync(listed with { State = OutboxState.Sending, Envelope = null }, ct);

        var loaded = Require.Ref(temp.Store.GetOutbox(id, ct), "the outbox row");
        Assert.Equal(OutboxState.Sending, loaded.State);
        Assert.Equal(
            new[] { "dave@example.org", "erin@example.org" },
            Addresses(Require.Ref(loaded.Envelope, "the preserved envelope").Bcc));
    }

    [Fact]
    public async Task SavingAnUnknownRowIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var failure = await Assert.ThrowsAsync<StoreException>(
            () => temp.Store.SaveOutboxAsync(Record(account, "raw"u8.ToArray()) with { Id = 987 }, ct));

        Assert.Equal(FailureCategory.NotFound, failure.Category);
    }

    [Fact]
    public async Task DueListingSkipsPermanentFailuresAndFutureRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var now = Created.AddHours(1);

        var due = await EnqueueAsync(temp, account, "due@example.com", raw: "a"u8.ToArray(), ct);
        var retryable = await EnqueueAsync(temp, account, "retry@example.com", raw: "b"u8.ToArray(), ct);
        var later = await EnqueueAsync(temp, account, "later@example.com", raw: "c"u8.ToArray(), ct);
        var permanent = await EnqueueAsync(temp, account, "dead@example.com", raw: "d"u8.ToArray(), ct);
        var exhausted = await EnqueueAsync(temp, account, "spent@example.com", raw: "e"u8.ToArray(), ct);

        // Every row here stands for a send a human already confirmed; an unconfirmed draft is
        // covered by UnconfirmedDraftTests, which asserts it is never due.
        foreach (var id in new[] { due, retryable, later, permanent, exhausted })
            await temp.Store.MarkOutboxConfirmedAsync(id, Created, ct);

        await SetAsync(temp, retryable, r => r with
        {
            State = OutboxState.Failed,
            Attempts = 1,
            NextAttemptUtc = now.AddMinutes(-1),
        }, ct);

        await SetAsync(temp, later, r => r with
        {
            State = OutboxState.Failed,
            Attempts = 1,
            NextAttemptUtc = now.AddHours(1),
        }, ct);

        await SetAsync(temp, permanent, r => r with
        {
            State = OutboxState.Failed,
            Attempts = 1,
            PermanentlyFailed = true,
            NextAttemptUtc = now.AddMinutes(-1),
        }, ct);

        await SetAsync(temp, exhausted, r => r with
        {
            State = OutboxState.Failed,
            Attempts = 5,
            NextAttemptUtc = now.AddMinutes(-1),
        }, ct);

        var ids = new List<long>();
        foreach (var row in temp.Store.ListDueOutbox(now, ct)) ids.Add(row.Id);

        Assert.Equal(new[] { due, retryable }, ids);
    }

    [Fact]
    public async Task ARowLeftSendingByACrashIsPickedUpAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var id = await temp.Store.EnqueueOutboxAsync(Record(account, "raw"u8.ToArray()), ct);
        await SetAsync(temp, id, r => r with { State = OutboxState.Sending, Attempts = 1 }, ct);

        var due = Assert.Single(temp.Store.ListDueOutbox(Created, ct));
        Assert.Equal(id, due.Id);
        Assert.Equal(OutboxState.Sending, due.State);
    }

    [Fact]
    public async Task ListingFiltersByStateAndFindsByMessageId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var queued = await EnqueueAsync(temp, account, "q@example.com", "a"u8.ToArray(), ct);
        var sent = await EnqueueAsync(temp, account, "s@example.com", "b"u8.ToArray(), ct);
        await SetAsync(temp, sent, r => r with { State = OutboxState.Sent, SmtpResponse = "250 ok" }, ct);

        Assert.Equal(queued, Assert.Single(temp.Store.ListOutbox(OutboxState.Queued, ct)).Id);
        Assert.Equal(sent, Assert.Single(temp.Store.ListOutbox(OutboxState.Sent, ct)).Id);

        var found = Require.Ref(
            temp.Store.FindOutboxByMessageId(MessageId.Parse("s@example.com"), ct),
            "the row found by Message-ID");

        Assert.Equal(sent, found.Id);
        Assert.Equal("b"u8.ToArray(), found.Raw);
    }

    private static OutboxRecord Record(AccountId account, byte[] raw, string messageId = "outbox-1@example.com") => new()
    {
        AccountId = account,
        MessageId = MessageId.Parse(messageId),
        State = OutboxState.Queued,
        Raw = raw,
        Attempts = 0,
        MaxAttempts = 5,
        CreatedUtc = Created,
        Envelope = new OutboxEnvelope
        {
            From = EmailAddress.Parse("alice@example.com"),
            To = [EmailAddress.Parse("bob@example.org")],
            Cc = [EmailAddress.Parse("carol@example.net")],
            Bcc = [EmailAddress.Parse("dave@example.org"), EmailAddress.Parse("erin@example.org")],
        },
    };

    private static Task<long> EnqueueAsync(
        TempStore temp,
        AccountId account,
        string messageId,
        byte[] raw,
        CancellationToken ct) =>
        temp.Store.EnqueueOutboxAsync(Record(account, raw, messageId), ct);

    private static async Task SetAsync(
        TempStore temp,
        long id,
        Func<OutboxRecord, OutboxRecord> change,
        CancellationToken ct)
    {
        var current = Require.Ref(temp.Store.GetOutbox(id, ct), "the outbox row");
        await temp.Store.SaveOutboxAsync(change(current), ct);
    }

    private static string[] Addresses(IReadOnlyList<EmailAddress> addresses)
    {
        var values = new string[addresses.Count];
        for (var i = 0; i < addresses.Count; i++) values[i] = addresses[i].Value;
        return values;
    }
}
