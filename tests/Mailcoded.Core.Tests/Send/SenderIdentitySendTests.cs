using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

public sealed class SenderIdentitySendTests
{
    private const string Own = "tester@example.com";

    private static DraftRequest Draft(string? from = null, string body = "Hello.", MessageId? inReplyTo = null) => new()
    {
        To = [EmailAddress.Parse("bob@example.org")],
        Subject = "Hello",
        BodyText = body,
        From = from is null ? null : EmailAddress.Parse(from),
        InReplyTo = inReplyTo,
    };

    private static async Task AddIdentityAsync(SendPathHarness harness, string address, string? name, string? replyTo, CancellationToken ct)
    {
        Assert.True(SenderIdentity.TryCreate(address, name, replyTo, out var identity, out var error), error);
        await harness.Store.EditIdentitiesAsync(harness.AccountId, list => SenderIdentities.With(list, identity), ct);
    }

    private static string Raw(SendPathHarness harness, SendPreview preview, CancellationToken ct) =>
        Encoding.UTF8.GetString(Require.Ref(harness.Store.GetOutbox(preview.OutboxId, ct), "the outbox row").Raw)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task StoreOriginalAsync(SendPathHarness harness, string to, string? cc, CancellationToken ct)
    {
        var folder = await StoreSeed.FolderAsync(harness.Store, harness.AccountId, "INBOX", FolderRole.Inbox, ct);
        await StoreSeed.MessageAsync(
            harness.Store,
            folder,
            StoreSeed.Envelope(1, subject: "Question", from: "ada@example.net", to: to, cc: cc, messageId: "orig-1@example.net"),
            ct);
    }

    [Fact]
    public async Task A_draft_without_a_from_goes_out_as_the_account_address()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);

        var preview = await harness.PreviewAsync(Draft(), ct);

        Assert.Equal(Own, preview.From.Value);
        Assert.Contains("From: Test Account <tester@example.com>", Raw(harness, preview, ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("boss@example.com")]
    [InlineData("tester@example.org")]
    [InlineData("tester@examp1e.com")]
    [InlineData("alias@example.com")]
    public async Task A_from_that_is_not_the_account_or_an_identity_is_refused_and_nothing_is_queued(string from)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);

        await Assert.ThrowsAsync<SenderNotAllowedException>(() => harness.PreviewAsync(Draft(from), ct));

        Assert.Empty(harness.Send.ListOutbox(null, ct));
        Assert.Equal(0, StoreQuery.Scalar(harness.Store, "SELECT COUNT(*) FROM confirm_tokens", ct));
    }

    [Fact]
    public async Task A_refusal_is_audited_without_the_body_and_the_exception_never_echoes_the_address()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);

        var failure = await Assert.ThrowsAsync<SenderNotAllowedException>(
            () => harness.PreviewAsync(Draft("ceo@example.org", "Wire the money."), ct));

        Assert.DoesNotContain("ceo@example.org", failure.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<ArgumentException>(failure);

        var rows = StoreQuery.Strings(harness.Store, "SELECT event || '|' || level || '|' || COALESCE(detail, '') FROM sync_log WHERE event = 'sender_denied'", ct);
        var row = Assert.Single(rows);
        Assert.Contains("|warn|", row, StringComparison.Ordinal);
        Assert.Contains("from=ceo@example.org", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Wire the money", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_identity_is_accepted_in_any_case_and_goes_out_with_its_name_and_reply_to()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org");
        await AddIdentityAsync(harness, "sales@example.com", "Sales Desk", "help@example.com", ct);

        var preview = await harness.PreviewAsync(Draft("SALES@Example.COM"), ct);
        var raw = Raw(harness, preview, ct);

        Assert.Equal("sales@example.com", preview.From.Value);
        Assert.Contains("From: Sales Desk <sales@example.com>", raw, StringComparison.Ordinal);
        Assert.Contains("Reply-To: help@example.com", raw, StringComparison.Ordinal);
        Assert.EndsWith("@example.com", preview.MessageId.Value, StringComparison.Ordinal);

        var result = await harness.SendDraftAsync(preview.OutboxId, preview.ConfirmToken, ct);
        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal("sales@example.com", Assert.Single(harness.Sender.Sends).From.Value);
    }

    [Fact]
    public async Task A_forgotten_identity_is_refused_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", null, null, ct);
        await harness.PreviewAsync(Draft("sales@example.com"), ct);

        await harness.Store.EditIdentitiesAsync(harness.AccountId, list => SenderIdentities.Without(list, EmailAddress.Parse("sales@example.com"), out _), ct);

        await Assert.ThrowsAsync<SenderNotAllowedException>(() => harness.PreviewAsync(Draft("sales@example.com"), ct));
    }

    [Fact]
    public async Task An_identity_with_no_name_uses_the_account_name()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "plain@example.com", null, null, ct);

        var preview = await harness.PreviewAsync(Draft("plain@example.com"), ct);

        var raw = Raw(harness, preview, ct);
        Assert.Contains("From: Test Account <plain@example.com>", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Reply-To:", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_is_sent_as_the_identity_the_original_was_addressed_to()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", "Sales", null, ct);
        await StoreOriginalAsync(harness, to: "list@example.net, Sales <SALES@example.com>", cc: Own, ct);

        var preview = await harness.PreviewAsync(Draft(inReplyTo: MessageId.Parse("orig-1@example.net")), ct);

        Assert.Equal("sales@example.com", preview.From.Value);
    }

    [Fact]
    public async Task A_reply_to_mail_sent_to_the_account_address_stays_the_account_address()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", null, null, ct);
        await StoreOriginalAsync(harness, to: Own, cc: "sales@example.com", ct);

        var preview = await harness.PreviewAsync(Draft(inReplyTo: MessageId.Parse("orig-1@example.net")), ct);

        Assert.Equal(Own, preview.From.Value);
    }

    [Fact]
    public async Task A_reply_to_mail_that_reached_the_account_by_blind_copy_uses_the_account_address()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", null, null, ct);
        await StoreOriginalAsync(harness, to: "list@example.net", cc: null, ct);

        var preview = await harness.PreviewAsync(Draft(inReplyTo: MessageId.Parse("orig-1@example.net")), ct);

        Assert.Equal(Own, preview.From.Value);
    }

    [Fact]
    public async Task A_reply_to_a_message_the_store_does_not_hold_uses_the_account_address()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", null, null, ct);

        var preview = await harness.PreviewAsync(Draft(inReplyTo: MessageId.Parse("never-seen@example.net")), ct);

        Assert.Equal(Own, preview.From.Value);
    }

    [Fact]
    public async Task An_explicit_from_beats_the_reply_default_and_is_still_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", null, null, ct);
        await StoreOriginalAsync(harness, to: "sales@example.com", cc: null, ct);

        var chosen = await harness.PreviewAsync(Draft(Own, inReplyTo: MessageId.Parse("orig-1@example.net")), ct);
        Assert.Equal(Own, chosen.From.Value);

        await Assert.ThrowsAsync<SenderNotAllowedException>(
            () => harness.PreviewAsync(Draft("boss@example.com", inReplyTo: MessageId.Parse("orig-1@example.net")), ct));
    }

    [Fact]
    public async Task The_original_being_addressed_to_an_unconfigured_address_never_widens_the_senders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", null, null, ct);
        await StoreOriginalAsync(harness, to: "ceo@example.org, attacker-chosen@example.net", cc: null, ct);

        var preview = await harness.PreviewAsync(Draft(inReplyTo: MessageId.Parse("orig-1@example.net")), ct);

        Assert.Equal(Own, preview.From.Value);
    }

    [Fact]
    public async Task A_message_with_the_same_id_in_another_account_does_not_pick_an_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);
        await AddIdentityAsync(harness, "sales@example.com", null, null, ct);

        var other = await StoreSeed.AccountAsync(harness.Store, ct, "someone@example.org");
        var folder = await StoreSeed.FolderAsync(harness.Store, other, "INBOX", FolderRole.Inbox, ct);
        await StoreSeed.MessageAsync(
            harness.Store, folder, StoreSeed.Envelope(1, to: "sales@example.com", messageId: "orig-1@example.net"), ct);

        var preview = await harness.PreviewAsync(Draft(inReplyTo: MessageId.Parse("orig-1@example.net")), ct);

        Assert.Equal(Own, preview.From.Value);
    }

    [Fact]
    public async Task Preparing_a_draft_for_an_unknown_account_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);

        var failure = await Assert.ThrowsAsync<Mailcoded.Core.Providers.StoreException>(
            () => harness.Send.PrepareDraftAsync(SendPathHarness.Caller, new AccountId(404), Draft(), ct));

        Assert.Equal(Mailcoded.Core.Providers.FailureCategory.NotFound, failure.Category);
    }
}
