using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

public sealed class SignaturePlacementTests
{
    private const string Quote = "On Mon, 1 Jan 2024 00:00:00 UTC, Ada wrote:\n\n> Hello\n";

    private static async Task<SendPathHarness> HarnessAsync(CancellationToken ct)
    {
        var harness = await SendPathHarness.CreateAsync(ct, "@example.org");
        await harness.Store.EditSignaturesAsync(
            harness.AccountId,
            book => book
                .Set("work", "Alex Example\nExample Pty")
                .Set("short", "Alex")
                .WithDefault(SignatureUse.New, "work")
                .WithDefault(SignatureUse.Reply, "short"),
            ct);
        return harness;
    }

    private static DraftRequest Draft(SignatureChoice choice, string body = "Thanks.", string? quoted = null, MessageId? inReplyTo = null) => new()
    {
        To = [EmailAddress.Parse("bob@example.org")],
        Subject = "Hello",
        BodyText = body,
        QuotedText = quoted,
        Signature = choice,
        InReplyTo = inReplyTo,
    };

    private static string Raw(SendPathHarness harness, SendPreview preview, CancellationToken ct) =>
        Encoding.UTF8.GetString(Require.Ref(harness.Store.GetOutbox(preview.OutboxId, ct), "the outbox row").Raw)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public async Task A_draft_that_asks_for_nothing_is_built_exactly_as_before_even_when_defaults_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var preview = await harness.PreviewAsync(Draft(SignatureChoice.None), ct);

        Assert.Equal("Thanks.", preview.BodyPreview);
        Assert.Null(preview.SignatureName);
        Assert.DoesNotContain("-- \n", Raw(harness, preview, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_new_message_default_is_inserted_after_the_separator_line()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var preview = await harness.PreviewAsync(Draft(SignatureChoice.UseDefault), ct);

        Assert.Equal("Thanks.\n\n-- \nAlex Example\nExample Pty\n", preview.BodyPreview);
        Assert.Equal("work", preview.SignatureName);
        Assert.Contains("\n\nThanks.\n\n-- \nAlex Example\nExample Pty\n", Raw(harness, preview, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_uses_the_reply_default_and_places_it_above_the_quote()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var preview = await harness.PreviewAsync(
            Draft(SignatureChoice.UseDefault, quoted: Quote, inReplyTo: MessageId.Parse("orig-1@example.net")), ct);

        Assert.Equal("Thanks.\n\n-- \nAlex\n\n" + Quote, preview.BodyPreview);
        Assert.Equal("short", preview.SignatureName);

        var raw = Raw(harness, preview, ct);
        Assert.True(raw.IndexOf("\n-- \nAlex\n", StringComparison.Ordinal) < raw.IndexOf("> Hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_named_signature_beats_the_default_and_works_for_new_and_reply_alike()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var fresh = await harness.PreviewAsync(Draft(SignatureChoice.Named("SHORT")), ct);
        var reply = await harness.PreviewAsync(
            Draft(SignatureChoice.Named("work"), quoted: Quote, inReplyTo: MessageId.Parse("orig-1@example.net")), ct);

        Assert.Equal("Thanks.\n\n-- \nAlex\n", fresh.BodyPreview);
        Assert.Equal("Thanks.\n\n-- \nAlex Example\nExample Pty\n\n" + Quote, reply.BodyPreview);
    }

    [Fact]
    public async Task An_unknown_signature_name_is_refused_and_nothing_is_queued()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => harness.PreviewAsync(Draft(SignatureChoice.Named("nope")), ct));

        Assert.IsNotType<SenderNotAllowedException>(failure);
        Assert.Empty(harness.Send.ListOutbox(null, ct));
    }

    [Fact]
    public async Task Asking_for_the_default_when_none_is_set_adds_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct);

        var preview = await harness.PreviewAsync(Draft(SignatureChoice.UseDefault), ct);

        Assert.Equal("Thanks.", preview.BodyPreview);
        Assert.Null(preview.SignatureName);
    }

    [Fact]
    public async Task An_implicit_default_is_skipped_when_the_author_already_signed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var preview = await harness.PreviewAsync(Draft(SignatureChoice.UseDefault, body: "Thanks.\n\n-- \nAlex by hand\n"), ct);

        Assert.Equal("Thanks.\n\n-- \nAlex by hand\n", preview.BodyPreview);
        Assert.Null(preview.SignatureName);
    }

    [Fact]
    public async Task An_explicit_name_is_always_inserted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var preview = await harness.PreviewAsync(Draft(SignatureChoice.Named("short"), body: "Thanks.\n\n-- \nAlex by hand\n"), ct);

        Assert.Equal(2, preview.BodyPreview.Split("\n-- \n").Length - 1);
    }

    [Fact]
    public async Task A_quote_without_a_signature_follows_the_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var preview = await harness.PreviewAsync(Draft(SignatureChoice.None, quoted: Quote), ct);

        Assert.Equal("Thanks.\n\n" + Quote, preview.BodyPreview);
    }

    [Fact]
    public async Task The_preview_reports_truncation_of_the_composed_body_not_the_typed_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);
        var body = new string('x', 1_990);

        var plain = await harness.PreviewAsync(Draft(SignatureChoice.None, body), ct);
        var signed = await harness.PreviewAsync(Draft(SignatureChoice.UseDefault, body), ct);

        Assert.False(plain.BodyTruncated);
        Assert.True(signed.BodyTruncated);
        Assert.Equal(2_000, signed.BodyPreview.Length);
    }

    [Fact]
    public async Task The_signed_body_is_what_the_confirm_digest_covers_and_what_is_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await HarnessAsync(ct);

        var preview = await harness.PreviewAsync(Draft(SignatureChoice.UseDefault), ct);
        await harness.SendDraftAsync(preview.OutboxId, preview.ConfirmToken, ct);

        var sent = Encoding.UTF8.GetString(Assert.Single(harness.Sender.Sends).Raw).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\n-- \nAlex Example\nExample Pty\n", sent, StringComparison.Ordinal);
        Assert.Equal(AuditText.Digest(Require.Ref(harness.Store.GetOutbox(preview.OutboxId, ct), "the outbox row").Raw), preview.Digest);
    }
}
