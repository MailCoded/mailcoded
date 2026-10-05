using MailKit;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Providers;
using MimeKit;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

public sealed class ImapEnvelopeSignalsTests
{
    [Fact]
    public void A_summary_without_headers_or_structure_is_unknown_not_false()
    {
        var signals = ImapEnvelopeSignals.Read(new MessageSummary(0), "alice@example.com");

        Assert.Null(signals.Headers);
        Assert.Null(signals.Automated);
        Assert.Null(signals.Encrypted);
    }

    [Fact]
    public void Fetched_headers_that_say_nothing_make_the_message_known_not_automated()
    {
        var summary = new MessageSummary(0) { Headers = new HeaderList(), Body = Basic("text", "plain") };

        var signals = ImapEnvelopeSignals.Read(summary, "alice@example.com");

        Assert.NotNull(signals.Headers);
        Assert.False(signals.Automated);
        Assert.False(signals.Encrypted);
    }

    [Fact]
    public void Only_the_wanted_headers_are_kept_and_they_are_matched_case_insensitively()
    {
        var headers = new HeaderList
        {
            { "list-id", "<l.example.net>" },
            { "References", "<a@example.com>" },
            { "AUTHENTICATION-RESULTS", "mx.example.org; dmarc=pass" },
            { "Received", "from nowhere" },
        };

        var signals = ImapEnvelopeSignals.Read(new MessageSummary(0) { Headers = headers }, "alice@example.com");

        Assert.Equal("<l.example.net>", signals.Headers!.ListId);
        Assert.Equal(new[] { "mx.example.org; dmarc=pass" }, signals.Headers.AuthenticationResults.ToArray());
        Assert.True(signals.Automated);
    }

    [Fact]
    public void A_multipart_report_in_the_structure_is_automated_without_any_header()
    {
        var report = Multipart("report", ("report-type", "delivery-status"));
        var summary = new MessageSummary(0) { Headers = new HeaderList(), Body = report };

        Assert.True(ImapEnvelopeSignals.Read(summary, "alice@example.com").Automated);
    }

    [Fact]
    public void Pgp_mime_in_the_structure_is_encrypted_at_envelope_time()
    {
        var body = Multipart("encrypted", ("protocol", "application/pgp-encrypted"));
        body.BodyParts.Add(Basic("application", "pgp-encrypted"));
        body.BodyParts.Add(Basic("application", "octet-stream"));

        Assert.True(ImapEnvelopeSignals.Read(new MessageSummary(0) { Body = body }, null).Encrypted);
    }

    [Theory]
    [InlineData("pkcs7-mime", "enveloped-data", true)]
    [InlineData("x-pkcs7-mime", "authEnveloped-data", true)]
    [InlineData("pkcs7-mime", "signed-data", false)]
    [InlineData("pkcs7-mime", null, false)]
    public void Smime_depends_on_the_smime_type_parameter(string subtype, string? smimeType, bool expected)
    {
        var part = smimeType is null ? Basic("application", subtype) : Basic("application", subtype, ("smime-type", smimeType));

        Assert.Equal(expected, ImapEnvelopeSignals.Read(new MessageSummary(0) { Body = part }, null).Encrypted);
    }

    [Fact]
    public void An_encrypted_part_below_the_top_level_is_found()
    {
        var mixed = Multipart("mixed");
        mixed.BodyParts.Add(Basic("text", "plain"));
        mixed.BodyParts.Add(Multipart("encrypted", ("protocol", "application/pgp-encrypted")));

        Assert.True(ImapEnvelopeSignals.Read(new MessageSummary(0) { Body = mixed }, null).Encrypted);
    }

    [Fact]
    public void An_encrypted_message_attached_as_message_rfc822_does_not_count()
    {
        var inner = Multipart("encrypted", ("protocol", "application/pgp-encrypted"));
        var attached = new BodyPartMessage(new ContentType("message", "rfc822"), "1") { Body = inner };
        var mixed = Multipart("mixed");
        mixed.BodyParts.Add(Basic("text", "plain"));
        mixed.BodyParts.Add(attached);

        Assert.False(ImapEnvelopeSignals.Read(new MessageSummary(0) { Body = mixed }, null).Encrypted);
    }

    [Fact]
    public void A_structure_nested_deeper_than_the_walk_limit_is_not_followed()
    {
        BodyPartMultipart current = Multipart("encrypted", ("protocol", "application/pgp-encrypted"));
        for (var i = 0; i < 20; i++)
        {
            var wrapper = Multipart("mixed");
            wrapper.BodyParts.Add(current);
            current = wrapper;
        }

        Assert.False(ImapEnvelopeSignals.Read(new MessageSummary(0) { Body = current }, null).Encrypted);
    }

    [Fact]
    public void A_wide_structure_is_cut_off_at_the_node_budget()
    {
        var wide = Multipart("mixed");
        for (var i = 0; i < 500; i++) wide.BodyParts.Add(Basic("text", "plain"));
        wide.BodyParts.Add(Multipart("encrypted", ("protocol", "application/pgp-encrypted")));

        Assert.False(ImapEnvelopeSignals.Read(new MessageSummary(0) { Body = wide }, null).Encrypted);
    }

    [Fact]
    public void A_robot_sender_is_automated_even_when_nothing_else_was_fetched()
    {
        Assert.True(ImapEnvelopeSignals.Read(new MessageSummary(0), "Service <no-reply@example.com>").Automated);
    }

    [Fact]
    public void The_envelope_fetch_request_accepts_the_header_list_alongside_the_references_item()
    {
        var request = new FetchRequest(
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.References,
            SignalHeaderCollector.FetchNames);

        var headers = Assert.IsType<HeaderSet>(request.Headers);
        Assert.Equal(9, headers.Count);
        Assert.Contains(headers, h => string.Equals(h, "Authentication-Results", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headers, h => string.Equals(h, "List-Unsubscribe-Post", StringComparison.OrdinalIgnoreCase));
    }

    private static BodyPartBasic Basic(string type, string subtype, params (string Name, string Value)[] parameters)
    {
        var contentType = new ContentType(type, subtype);
        foreach (var (name, value) in parameters) contentType.Parameters.Add(name, value);
        return new BodyPartBasic(contentType, "1");
    }

    private static BodyPartMultipart Multipart(string subtype, params (string Name, string Value)[] parameters)
    {
        var contentType = new ContentType("multipart", subtype);
        foreach (var (name, value) in parameters) contentType.Parameters.Add(name, value);
        return new BodyPartMultipart(contentType, "1");
    }
}
