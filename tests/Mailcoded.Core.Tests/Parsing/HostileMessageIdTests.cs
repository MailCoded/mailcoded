using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class HostileMessageIdTests
{
    private const string HostileWire = "20250101.abc@mail..example.com";

    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sent = new(2026, 2, 3, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("<20250101.abc@mail..example.com>")]
    [InlineData("<abc@example..com>")]
    [InlineData("<abc@.example.com>")]
    [InlineData("<abc@example.com.>")]
    [InlineData("<a..b@example.com>")]
    [InlineData("<.abc@example.com>")]
    [InlineData("<abc.@example.com>")]
    [InlineData("<@example.com>")]
    [InlineData("<abc@>")]
    [InlineData("<abc@exa[mple.com>")]
    [InlineData("<a,b@example.com>")]
    public void AMsgIdNoParserCanReadBackIsNotAdopted(string wire)
    {
        Assert.False(
            MessageId.TryParse(wire, out _),
            "An unusable Message-ID must not become a MessageId: it would reach In-Reply-To on a reply "
            + "and make the message permanently unreplyable.");
    }

    [Theory]
    [InlineData("<abc@example.com>", "abc@example.com")]
    [InlineData("<a.b.c@Mail.Example.CO.NZ>", "a.b.c@mail.example.co.nz")]
    [InlineData("<CAG+dead=beef@mail.example.com>", "CAG+dead=beef@mail.example.com")]
    public void AUsableMsgIdStillParsesAndCanBeWrittenIntoAReply(string wire, string expected)
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.True(MessageId.TryParse(wire, out var id));
        Assert.Equal(expected, id.Value);

        var raw = MessageBuilder.Build(Reply(id), ct);
        Assert.Contains("In-Reply-To: <" + expected + ">", Encoding.ASCII.GetString(raw), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<abc@[192.0.2.1]>")]
    [InlineData("<no-domain-part>")]
    [InlineData("<\"quoted\"@example.com>")]
    public void AnUnusualButParsableMsgIdIsStillAccepted(string wire) =>
        Assert.True(MessageId.TryParse(wire, out _));

    [Fact]
    public void AMessageCarryingAnUnusableMsgIdStillSyncsAndStaysReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var parsed = MessageParser.Default.Parse(Message(HostileWire), InternalDate, ct);

        Assert.Null(parsed.MessageId);
        Assert.Contains("unparsable-message-id", parsed.ParseWarnings);
        Assert.Equal("Unusable Message-ID", parsed.Subject);
        Assert.Contains("still readable", parsed.BodyText, StringComparison.Ordinal);
        Assert.Null(parsed.InReplyTo);
        Assert.Empty(parsed.References);
    }

    [Fact]
    public void AReplyToThatMessageBuildsBecauseTheUnusableIdNeverBecameOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var parsed = MessageParser.Default.Parse(Message(HostileWire), InternalDate, ct);

        var spec = Reply(MessageId.Parse("draft-unusable-parent@example.com")) with
        {
            InReplyTo = parsed.MessageId,
            References = [],
        };

        var raw = Encoding.ASCII.GetString(MessageBuilder.Build(spec, ct));
        Assert.DoesNotContain("In-Reply-To:", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("mail..example.com", raw, StringComparison.Ordinal);
    }

    private static byte[] Message(string messageId) => Encoding.ASCII.GetBytes(
        "From: hostile@example.net\r\n"
        + "To: bob@example.org\r\n"
        + "Subject: Unusable Message-ID\r\n"
        + "Date: Mon, 20 Jan 2025 21:00:00 +0000\r\n"
        + "Message-ID: <" + messageId + ">\r\n"
        + "MIME-Version: 1.0\r\n"
        + "Content-Type: text/plain; charset=us-ascii\r\n"
        + "\r\n"
        + "The body is still readable even though the Message-ID is not.\r\n");

    private static DraftSpec Reply(MessageId parent) => new()
    {
        From = EmailAddress.Parse("alice@example.com"),
        To = [EmailAddress.Parse("bob@example.org")],
        Subject = "Re: Unusable Message-ID",
        BodyText = "Replying anyway.",
        MessageId = MessageId.Parse("draft-reply-1@example.com"),
        InReplyTo = parent,
        References = [parent],
        DateUtc = Sent,
    };
}
