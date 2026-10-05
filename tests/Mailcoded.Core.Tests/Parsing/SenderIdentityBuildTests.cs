using System.Text;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class SenderIdentityBuildTests
{
    private static readonly DateTimeOffset Sent = new(2026, 2, 3, 9, 30, 0, TimeSpan.Zero);

    private static DraftSpec Draft(string body = "Hello.", EmailAddress? replyTo = null) => new()
    {
        From = EmailAddress.Parse("sales@example.com"),
        FromDisplayName = "Sales Desk",
        ReplyTo = replyTo,
        To = [EmailAddress.Parse("bob@example.org")],
        Subject = "Hi",
        BodyText = body,
        MessageId = MessageId.Parse("identity-1@example.com"),
        DateUtc = Sent,
    };

    [Fact]
    public void A_reply_to_on_the_spec_becomes_one_reply_to_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var raw = MessageBuilder.Build(Draft(replyTo: EmailAddress.Parse("team@example.com")), ct);
        var parsed = MessageParser.Default.Parse(raw, default(DateTimeOffset), ct);

        Assert.Contains("team@example.com", Assert.IsType<string>(parsed.ReplyTo), StringComparison.Ordinal);
        Assert.Contains("sales@example.com", Assert.IsType<string>(parsed.From), StringComparison.Ordinal);
        Assert.Contains("Sales Desk", parsed.From, StringComparison.Ordinal);
        Assert.Equal(1, CountHeader(raw, "Reply-To"));
    }

    [Fact]
    public void Without_a_reply_to_no_header_is_written()
    {
        var raw = MessageBuilder.Build(Draft(), TestContext.Current.CancellationToken);

        Assert.Equal(0, CountHeader(raw, "Reply-To"));
    }

    [Fact]
    public void A_signature_keeps_its_separator_line_with_the_trailing_space_on_the_wire()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = DraftBody.Compose("Thanks.", "Alex Example\nExample Pty", "On Mon, Ada wrote:\n\n> Hi\n");

        var raw = MessageBuilder.Build(Draft(body), ct);
        var text = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("\n-- \nAlex Example\nExample Pty\n", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("-- \n", StringComparison.Ordinal) < text.IndexOf("> Hi", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_ascii_signature_survives_a_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = DraftBody.Compose("Hi", "山田 太郎\nExample 株式会社", null);

        var parsed = MessageParser.Default.Parse(MessageBuilder.Build(Draft(body), ct), default(DateTimeOffset), ct);

        Assert.Contains("山田 太郎", parsed.BodyText, StringComparison.Ordinal);
        Assert.Contains("Example 株式会社", parsed.BodyText, StringComparison.Ordinal);
    }

    private static int CountHeader(byte[] raw, string name)
    {
        var text = Encoding.ASCII.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal);
        var headerEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
        var count = 0;

        foreach (var line in text[..headerEnd].Split('\n'))
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) count++;

        return count;
    }
}
