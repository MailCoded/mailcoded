using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class MessageParserFixtureTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingMessageIdIsReportedNotInvented()
    {
        var parsed = ParseFixture("003-no-message-id.eml");

        Assert.Null(parsed.MessageId);
        Assert.Contains("missing-message-id", parsed.ParseWarnings);
        Assert.Equal("Sent by an ancient mailer", parsed.Subject);
    }

    [Fact]
    public void DuplicateMessageIdHeadersAreFlaggedAndOneIsChosen()
    {
        var parsed = ParseFixture("004-duplicate-message-id.eml");

        Assert.Contains("duplicate-message-id", parsed.ParseWarnings);
        var id = Require.Value(parsed.MessageId, "the chosen Message-ID").Value;
        Assert.Contains(id, new[] { "004-dup@example.com", "004-dup-second@example.com" });
    }

    [Fact]
    public void AMissingDateFallsBackToInternalDate()
    {
        var parsed = ParseFixture("005-missing-date.eml");

        Assert.Contains("missing-date", parsed.ParseWarnings);
        Assert.Equal(InternalDate, parsed.DateUtc);
    }

    [Fact]
    public void AnAbsurdDateFallsBackToInternalDate()
    {
        var parsed = ParseFixture("006-absurd-date.eml");

        Assert.Equal(InternalDate, parsed.DateUtc);
        Assert.True(
            parsed.ParseWarnings.Contains("implausible-date") || parsed.ParseWarnings.Contains("unparsable-date"),
            "A year-30827 Date header must be rejected with a warning.");
    }

    [Fact]
    public void WithNoInternalDateAnUnusableDateBecomesTheEpoch()
    {
        var ct = TestContext.Current.CancellationToken;
        var parsed = MessageParser.Default.Parse(Fixtures.Read("005-missing-date.eml"), default(DateTimeOffset), ct);

        Assert.Equal(DateTimeOffset.UnixEpoch, parsed.DateUtc);
    }

    [Fact]
    public void RawEightBitHeadersAndBodiesDecodeWithoutThrowing()
    {
        var parsed = ParseFixture("008-raw-8bit-subject.eml");

        // The declared charset disagrees with the bytes on disk; the ASCII skeleton must still survive.
        var subject = Require.Ref(parsed.Subject, "the subject");
        Assert.Contains("union", subject, StringComparison.Ordinal);
        Assert.Contains("vue", subject, StringComparison.Ordinal);
        Assert.Contains("signaler", parsed.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain(parsed.ParseWarnings, w => w.StartsWith("body-part-unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public void Rfc2047EncodedCjkHeadersAreDecoded()
    {
        var parsed = ParseFixture("009-rfc2047-subject.eml");

        Assert.Equal("見積もりの件について", parsed.Subject);
        var from = Require.Ref(parsed.From, "the From header");
        Assert.Contains("山田太郎", from, StringComparison.Ordinal);
        Assert.Contains("yamada@example.jp", from, StringComparison.Ordinal);
        Assert.Contains("見積もりの件についてご連絡いたします", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCharsetFallsBackInsteadOfThrowing()
    {
        var parsed = ParseFixture("010-unknown-charset.eml");

        Assert.Equal("Unknown charset", parsed.Subject);
        Assert.Contains("The declared charset does not exist.", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedPrintableLatin1IsDecodedInHeaderAndBody()
    {
        var parsed = ParseFixture("021-quoted-printable.eml");

        Assert.Equal("Café meeting", parsed.Subject);
        Assert.Contains("Let's meet at the café", parsed.BodyText, StringComparison.Ordinal);
        Assert.Contains("résumés", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptAndStyleContentNeverReachesTheIndexedBodyText()
    {
        var parsed = ParseFixture("011-html-only.eml");

        Assert.Contains("Big news", parsed.BodyText, StringComparison.Ordinal);
        Assert.Contains("Some text with an & entity.", parsed.BodyText, StringComparison.Ordinal);
        Assert.Contains("bad link", parsed.BodyText, StringComparison.Ordinal);

        Assert.DoesNotContain("alert(1)", parsed.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("alert(2)", parsed.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", parsed.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("color:red", parsed.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", parsed.BodyText, StringComparison.OrdinalIgnoreCase);

        // The raw HTML is handed to the client untouched; sanitizing is the client's job, not the parser's.
        Assert.Contains("<script>", Require.Ref(parsed.BodyHtml, "the HTML alternative"), StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroByteBodyIsEmptyAndFlagged()
    {
        var parsed = ParseFixture("012-zero-byte-body.eml");

        Assert.Equal(string.Empty, parsed.BodyText);
        Assert.Null(parsed.BodyHtml);
        Assert.Empty(parsed.Attachments);
        Assert.Contains("empty-body", parsed.ParseWarnings);
        Assert.Equal("Headers only", parsed.Subject);
    }

    [Fact]
    public void ANestedRfc822PartIsBothAnAttachmentAndSearchableText()
    {
        var parsed = ParseFixture("013-nested-rfc822.eml");

        var nested = Assert.Single(parsed.Attachments);
        Assert.Equal("message/rfc822", nested.MimeType);
        Assert.Equal(0, nested.Index);

        Assert.Contains("See the forwarded message below.", parsed.BodyText, StringComparison.Ordinal);
        Assert.Contains("The quarterly report is attached", parsed.BodyText, StringComparison.Ordinal);
        Assert.True(parsed.HasAttachments);
    }

    [Fact]
    public void AttachmentsAreEnumeratedWithTypeNameAndSize()
    {
        var parsed = ParseFixture("014-attachment-pdf.eml");

        var attachment = Assert.Single(parsed.Attachments);
        Assert.Equal(0, attachment.Index);
        Assert.Equal("application/pdf", attachment.MimeType);
        Assert.Equal("invoice-4471.pdf", attachment.FileName);
        Assert.False(attachment.IsInline);
        Assert.Null(attachment.ContentId);
        Assert.True(attachment.Size > 0, "The attachment size must come from the part headers.");
        Assert.Contains("Invoice attached.", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void APathTraversalFilenameIsKeptVerbatimButSavedAsASafeLeaf()
    {
        var parsed = ParseFixture("015-attachment-hostile-filename.eml");
        var attachment = Assert.Single(parsed.Attachments);

        Assert.Equal("../../../../etc/cron.d/pwn", attachment.FileName);

        var posix = AttachmentNaming.ToSaveAsFileName(attachment.FileName, attachment.Index, attachment.MimeType);
        Assert.Equal("pwn", posix);

        var windows = AttachmentNaming.ToSaveAsFileName(
            @"..\..\..\Windows\System32\pwn.exe", attachment.Index, attachment.MimeType);
        Assert.Equal("pwn.exe", windows);

        Assert.Equal("hosts", AttachmentNaming.ToSaveAsFileName(@"C:\Windows\System32\drivers\etc\hosts", 0, null));
        Assert.Equal("_CON.txt", AttachmentNaming.ToSaveAsFileName("CON.txt", 0, null));
        Assert.Equal("attachment-3.bin", AttachmentNaming.ToSaveAsFileName("../../", 3, null));
        Assert.Equal("attachment-4.pdf", AttachmentNaming.ToSaveAsFileName(null, 4, "application/pdf"));
    }

    [Fact]
    public void AnInlineImageKeepsItsContentIdAndInlineFlag()
    {
        var parsed = ParseFixture("016-inline-cid-image.eml");

        var image = Assert.Single(parsed.Attachments);
        Assert.Equal("image/gif", image.MimeType);
        Assert.Equal("logo.gif", image.FileName);
        Assert.Equal("logo016@example.com", image.ContentId);
        Assert.True(image.IsInline);
        Assert.Contains("Regards,", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupAddressesAreFlattenedIntoTheHeaderText()
    {
        var parsed = ParseFixture("028-group-address.eml");

        var to = Require.Ref(parsed.To, "the To header");
        Assert.Contains("alice@example.com", to, StringComparison.Ordinal);
        Assert.Contains("bob@example.org", to, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", to, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", to, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedAddressesDegradeRatherThanThrow()
    {
        var parsed = ParseFixture("029-malformed-address.eml");

        Assert.Equal("Malformed addresses everywhere", parsed.Subject);
        Assert.Contains("bob@example.org", Require.Ref(parsed.To, "the To header"), StringComparison.Ordinal);
        Assert.Contains("Address parsing must degrade", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedMultipartStillYieldsTheTextItDidReceive()
    {
        var parsed = ParseFixture("031-unterminated-boundary.eml");

        Assert.Equal("Truncated mid-part", parsed.Subject);
        Assert.Contains("The closing boundary never arrives.", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredUtf7BodyIsNeverDecodedIntoScriptContent()
    {
        var parsed = ParseFixture("035-utf7-declared.eml");

        Assert.Contains("+ADw-script+AD4-", parsed.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", parsed.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script", parsed.BodyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeaderTextIsAlwaysSingleLine()
    {
        foreach (var name in Fixtures.EmlFileNames())
        {
            var parsed = ParseFixture(name);

            foreach (var header in new[] { parsed.Subject, parsed.From, parsed.To, parsed.Cc, parsed.ReplyTo })
            {
                if (header is null) continue;
                Assert.DoesNotContain("\n", header, StringComparison.Ordinal);
                Assert.DoesNotContain("\r", header, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ThreadingInputsSurviveTheParse()
    {
        var parsed = ParseFixture("017-thread-reply.eml");

        Assert.NotNull(parsed.MessageId);
        Assert.True(parsed.References.Count > 0 || parsed.InReplyTo is not null);

        foreach (var reference in parsed.References)
            Assert.False(string.IsNullOrWhiteSpace(reference.Value));
    }

    private static ParsedMessage ParseFixture(string fileName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = Fixtures.Open(fileName);
        return MessageParser.Default.Parse(stream, InternalDate, ct);
    }
}
