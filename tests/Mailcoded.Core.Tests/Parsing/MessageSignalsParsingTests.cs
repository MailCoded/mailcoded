using System.Text;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class MessageSignalsParsingTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("001-plain-simple.eml", false, false)]
    [InlineData("002-multipart-alternative.eml", false, false)]
    [InlineData("013-nested-rfc822.eml", false, false)]
    [InlineData("023-smime-opaque.eml", false, false)]
    [InlineData("038-list-mail.eml", true, false)]
    [InlineData("039-auto-reply.eml", true, false)]
    [InlineData("040-dsn-report.eml", true, false)]
    [InlineData("041-pgp-mime.eml", false, true)]
    [InlineData("042-smime-enveloped.eml", false, true)]
    [InlineData("043-inline-pgp.eml", false, true)]
    [InlineData("044-forwarded-encrypted.eml", false, false)]
    [InlineData("045-signal-header-flood.eml", true, false)]
    public void Fixtures_carry_the_expected_signals(string fixture, bool automated, bool encrypted)
    {
        var parsed = Parse(fixture);

        Assert.Equal(automated, parsed.Automated);
        Assert.Equal(encrypted, parsed.Encrypted);
        Assert.NotNull(parsed.SignalHeaders);
    }

    [Fact]
    public void List_mail_exposes_every_header_a_later_feature_needs_as_a_plain_string()
    {
        var headers = Require.Ref(Parse("038-list-mail.eml").SignalHeaders, "the signal headers");

        Assert.Equal("Announcements <announce.lists.example.net>", headers.ListId);
        Assert.Contains("mailto:unsubscribe@lists.example.net", headers.ListUnsubscribe, StringComparison.Ordinal);
        Assert.Contains("https://lists.example.net/unsubscribe/abc123", headers.ListUnsubscribe, StringComparison.Ordinal);
        Assert.Equal("List-Unsubscribe=One-Click", headers.ListUnsubscribePost);
        Assert.Equal("bulk", headers.Precedence);
        Assert.Null(headers.AutoSubmitted);
    }

    [Fact]
    public void Authentication_results_keep_every_instance_topmost_first()
    {
        var results = Require.Ref(Parse("038-list-mail.eml").SignalHeaders, "the signal headers").AuthenticationResults;

        Assert.Equal(2, results.Count);
        Assert.StartsWith("mx.example.org;", results[0], StringComparison.Ordinal);
        Assert.Contains("dmarc=pass", results[0], StringComparison.Ordinal);
        Assert.StartsWith("relay.example.net;", results[1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_auto_reply_keeps_its_comment_and_its_vendor_header()
    {
        var headers = Require.Ref(Parse("039-auto-reply.eml").SignalHeaders, "the signal headers");

        Assert.Equal("auto-replied (vacation)", headers.AutoSubmitted);
        Assert.Equal("yes", headers.XAutoreply);
    }

    [Fact]
    public void Hostile_signal_headers_are_bounded_and_the_contradictory_pair_still_counts()
    {
        var parsed = Parse("045-signal-header-flood.eml");
        var headers = Require.Ref(parsed.SignalHeaders, "the signal headers");

        Assert.Equal(SignalHeaderCollector.MaxAuthenticationResults, headers.AuthenticationResults.Count);
        Assert.StartsWith("mx0.example.net", headers.AuthenticationResults[0], StringComparison.Ordinal);
        Assert.True(headers.ListId!.Length <= SignalHeaderCollector.MaxValueChars);
        Assert.Equal("no, auto-generated", headers.AutoSubmitted);
        Assert.True(parsed.Automated);
    }

    [Fact]
    public void A_signed_but_not_enveloped_smime_message_is_not_marked_encrypted()
    {
        var parsed = Parse("023-smime-opaque.eml");

        Assert.False(parsed.Encrypted);
        Assert.Contains("encrypted-part-skipped", parsed.ParseWarnings);
    }

    [Fact]
    public void An_encrypted_message_forwarded_as_an_attachment_does_not_mark_the_outer_message()
    {
        var parsed = Parse("044-forwarded-encrypted.eml");

        Assert.False(parsed.Encrypted);
        Assert.Contains("this note is not", parsed.BodyText.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Armor_in_an_html_part_is_not_scanned()
    {
        var raw = Message(
            "Content-Type: text/html; charset=utf-8",
            "<pre>\n-----BEGIN PGP MESSAGE-----\nabc\n-----END PGP MESSAGE-----\n</pre>");

        Assert.False(ParseText(raw).Encrypted);
    }

    [Fact]
    public void Armor_in_a_plain_alternative_is_found_inside_multipart_alternative()
    {
        var raw = Encoding.ASCII.GetBytes(
            "From: a@example.com\r\nTo: b@example.org\r\nSubject: x\r\nMIME-Version: 1.0\r\n"
            + "Content-Type: multipart/alternative; boundary=\"b\"\r\n\r\n"
            + "--b\r\nContent-Type: text/plain\r\n\r\n-----BEGIN PGP MESSAGE-----\r\nabc\r\n-----END PGP MESSAGE-----\r\n"
            + "--b\r\nContent-Type: text/html\r\n\r\n<p>x</p>\r\n--b--\r\n");

        Assert.True(MessageParser.Default.Parse(raw, InternalDate, TestContext.Current.CancellationToken).Encrypted);
    }

    [Theory]
    [InlineData("\"noreply@evil.example\" <person@example.com>", false)]
    [InlineData("Alerts <noreply@example.com>", true)]
    [InlineData("=?utf-8?q?no-reply?= <no-reply@example.com>", true)]
    public void The_sender_rule_uses_the_parsed_from_not_the_display_name(string from, bool automated)
    {
        var raw = Encoding.ASCII.GetBytes($"From: {from}\r\nTo: b@example.org\r\nSubject: x\r\n\r\nbody\r\n");

        Assert.Equal(automated, MessageParser.Default.Parse(raw, InternalDate, TestContext.Current.CancellationToken).Automated);
    }

    [Fact]
    public void A_message_with_no_signals_at_all_is_known_not_automated_rather_than_unknown()
    {
        var parsed = ParseText(Message("Content-Type: text/plain", "hello"));

        Assert.False(parsed.Automated);
        Assert.False(parsed.Encrypted);
    }

    [Fact]
    public void Both_parse_entry_points_agree_on_the_signals()
    {
        var ct = TestContext.Current.CancellationToken;

        foreach (var fixture in new[] { "038-list-mail.eml", "041-pgp-mime.eml", "043-inline-pgp.eml" })
        {
            var fromBytes = MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, ct);

            using var stream = Fixtures.Open(fixture);
            var hashed = MessageParser.Default.ParseAndHash(stream, InternalDate, ct).Message;

            Assert.Equal(fromBytes.Automated, hashed.Automated);
            Assert.Equal(fromBytes.Encrypted, hashed.Encrypted);
            Assert.Equal(fromBytes.SignalHeaders!.ListId, hashed.SignalHeaders!.ListId);
        }
    }

    private static ParsedMessage Parse(string fixture) =>
        MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, TestContext.Current.CancellationToken);

    private static ParsedMessage ParseText(string raw) =>
        MessageParser.Default.Parse(Encoding.UTF8.GetBytes(raw), InternalDate, TestContext.Current.CancellationToken);

    private static string Message(string contentType, string body) =>
        "From: a@example.com\r\nTo: b@example.org\r\nSubject: x\r\nMIME-Version: 1.0\r\n"
        + contentType + "\r\n\r\n" + body.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
}
