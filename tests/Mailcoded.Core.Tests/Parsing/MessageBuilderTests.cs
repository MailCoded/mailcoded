using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class MessageBuilderTests
{
    private static readonly DateTimeOffset Sent = new(2026, 2, 3, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ADraftRoundTripsThroughBuildAndParse()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft();

        var raw = MessageBuilder.Build(spec, ct);
        var parsed = MessageParser.Default.Parse(raw, default(DateTimeOffset), ct);

        Assert.Equal(spec.MessageId, Require.Value(parsed.MessageId, "the Message-ID"));
        Assert.Equal(spec.InReplyTo, parsed.InReplyTo);
        Assert.Equal(
            new[] { "001-plain@example.com", "017-reply@example.org" },
            MessageIds(parsed.References));

        Assert.Equal("Re: Quarterly report", parsed.Subject);
        Assert.Contains("alice@example.com", Require.Ref(parsed.From, "the From header"), StringComparison.Ordinal);
        Assert.Contains("bob@example.org", Require.Ref(parsed.To, "the To header"), StringComparison.Ordinal);
        Assert.Contains("carol@example.net", Require.Ref(parsed.Cc, "the Cc header"), StringComparison.Ordinal);
        Assert.Equal("Numbers attached in the next one.", parsed.BodyText);
        Assert.Equal(Sent, parsed.DateUtc);
        Assert.Empty(parsed.Attachments);
    }

    [Fact]
    public void TheMessageIdIsTakenFromTheSpecNeverMintedAtBuildTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft();

        var first = MessageBuilder.Build(spec, ct);
        var second = MessageBuilder.Build(spec, ct);

        Assert.Equal(
            Require.Value(MessageParser.Default.Parse(first, default(DateTimeOffset), ct).MessageId, "the first Message-ID"),
            Require.Value(MessageParser.Default.Parse(second, default(DateTimeOffset), ct).MessageId, "the second Message-ID"));

        Assert.Contains("<draft-outbox-1@example.com>", Encoding.ASCII.GetString(first), StringComparison.Ordinal);
    }

    [Fact]
    public void ADisplayNameIsCarriedOnTheFromHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft() with { FromDisplayName = "Alice Example" };

        var parsed = MessageParser.Default.Parse(MessageBuilder.Build(spec, ct), default(DateTimeOffset), ct);

        Assert.Contains("Alice Example", Require.Ref(parsed.From, "the From header"), StringComparison.Ordinal);
        Assert.Contains("alice@example.com", Require.Ref(parsed.From, "the From header"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Re: hello\r\nBcc: evil@example.net")]
    [InlineData("Re: hello\nBcc: evil@example.net")]
    [InlineData("Re: hello\rBcc: evil@example.net")]
    public void ASubjectCarryingAControlCharacterIsRejected(string subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft() with { Subject = subject };

        Assert.Throws<ArgumentException>(() => { _ = MessageBuilder.Build(spec, ct); });
    }

    [Fact]
    public void ASubjectCarryingANulOrBellIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;

        foreach (var control in new[] { '\u0000', '\u0007', '\u000B' })
        {
            var spec = Draft() with { Subject = "Re: hello" + control };
            Assert.Throws<ArgumentException>(() => { _ = MessageBuilder.Build(spec, ct); });
        }
    }

    [Fact]
    public void ATabInASubjectIsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft() with { Subject = "Re:\tQuarterly report" };

        Assert.NotEmpty(MessageBuilder.Build(spec, ct));
    }

    [Fact]
    public void ADisplayNameCarryingALineBreakIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft() with { FromDisplayName = "Alice\r\nBcc: evil@example.net" };

        Assert.Throws<ArgumentException>(() => { _ = MessageBuilder.Build(spec, ct); });
    }

    [Fact]
    public void AnAddressCanNeverCarryALineBreakInTheFirstPlace()
    {
        Assert.False(EmailAddress.TryParse("evil@example.net\r\nBcc: other@example.net", out _));
        Assert.False(EmailAddress.TryParse("evil@example.net\nBcc: other@example.net", out _));
        Assert.False(EmailAddress.TryParse("evil@example.net\u0000", out _));
    }

    [Fact]
    public void ADraftWithoutAPreAssignedMessageIdIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft() with { MessageId = default };

        Assert.Throws<ArgumentException>(() => { _ = MessageBuilder.Build(spec, ct); });
    }

    [Fact]
    public void ADraftWithoutACallerSuppliedDateIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft() with { DateUtc = default };

        Assert.Throws<ArgumentException>(() => { _ = MessageBuilder.Build(spec, ct); });
    }

    [Fact]
    public void ADraftWithNoRecipientsIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft() with { To = [], Cc = [], Bcc = [] };

        Assert.Throws<ArgumentException>(() => { _ = MessageBuilder.Build(spec, ct); });
    }

    [Fact]
    public void SmtpUtf8IsRequiredOnlyWhenAnAddressIsNonAscii()
    {
        Assert.False(MessageBuilder.RequiresSmtpUtf8(Draft()));

        var international = Draft() with { To = [EmailAddress.Parse("张三@example.cn")] };
        Assert.True(MessageBuilder.RequiresSmtpUtf8(international));

        var internationalBcc = Draft() with { Bcc = [EmailAddress.Parse("bob@例え.com")] };
        Assert.True(MessageBuilder.RequiresSmtpUtf8(internationalBcc));
    }

    [Fact]
    public void WritingToAStreamProducesTheSameBytesAsBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Draft();

        using var buffer = new MemoryStream();
        MessageBuilder.WriteTo(spec, buffer, ct);

        Assert.Equal(MessageBuilder.Build(spec, ct), buffer.ToArray());
    }

    private static DraftSpec Draft() => new()
    {
        From = EmailAddress.Parse("alice@example.com"),
        To = [EmailAddress.Parse("bob@example.org")],
        Cc = [EmailAddress.Parse("carol@example.net")],
        Bcc = [EmailAddress.Parse("dave@example.org")],
        Subject = "Re: Quarterly report",
        BodyText = "Numbers attached in the next one.",
        MessageId = MessageId.Parse("draft-outbox-1@example.com"),
        InReplyTo = MessageId.Parse("017-reply@example.org"),
        References = [MessageId.Parse("001-plain@example.com"), MessageId.Parse("017-reply@example.org")],
        DateUtc = Sent,
    };

    private static string[] MessageIds(IReadOnlyList<MessageId> ids)
    {
        var values = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++) values[i] = ids[i].Value;
        return values;
    }
}
