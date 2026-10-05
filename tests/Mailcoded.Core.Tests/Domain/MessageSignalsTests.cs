using System.Diagnostics;
using Mailcoded.Core.Domain.Signals;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class MessageSignalsTests
{
    private static readonly SignalHeaders NoHeaders = SignalHeaders.None;

    [Theory]
    [InlineData("List-Id")]
    [InlineData("List-Unsubscribe")]
    [InlineData("X-Autoreply")]
    [InlineData("X-Autorespond")]
    [InlineData("X-Failed-Recipients")]
    public void A_present_header_alone_marks_the_message_automated_even_when_it_is_empty(string header)
    {
        var headers = Collect((header, "<x@example.com>"));
        Assert.True(MessageSignals.IsAutomated(headers, "alice@example.com", null));

        var empty = Collect((header, ""));
        Assert.True(MessageSignals.IsAutomated(empty, "alice@example.com", null));
    }

    [Theory]
    [InlineData("bulk", true)]
    [InlineData("list", true)]
    [InlineData("junk", true)]
    [InlineData("BULK", true)]
    [InlineData("  Bulk  ", true)]
    [InlineData("bulk (mass mailing)", true)]
    [InlineData("first-class", false)]
    [InlineData("special-delivery", false)]
    [InlineData("", false)]
    [InlineData("bulkmail", false)]
    [InlineData("first-class, bulk", true)]
    public void Precedence_is_bulk_only_for_bulk_list_or_junk(string value, bool expected)
    {
        Assert.Equal(expected, MessageSignals.IsAutomated(Collect(("Precedence", value)), "alice@example.com", null));
    }

    [Theory]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    [InlineData(" no ", false)]
    [InlineData("no (hand written)", false)]
    [InlineData("", false)]
    [InlineData("auto-generated", true)]
    [InlineData("auto-replied", true)]
    [InlineData("auto-notified", true)]
    [InlineData("auto-replied; owner-email=a@example.com", true)]
    [InlineData("no; auto-replied", false)]
    [InlineData("no, auto-generated", true)]
    [InlineData("anything-else", true)]
    public void Auto_submitted_is_automated_unless_every_value_is_no(string value, bool expected)
    {
        Assert.Equal(expected, MessageSignals.IsAutoSubmitted(value));
        Assert.Equal(expected, MessageSignals.IsAutomated(Collect(("Auto-Submitted", value)), "alice@example.com", null));
    }

    [Fact]
    public void A_contradictory_duplicate_auto_submitted_pair_cannot_hide_the_automatic_one()
    {
        var headers = Collect(("Auto-Submitted", "no"), ("Auto-Submitted", "auto-generated"));

        Assert.True(MessageSignals.IsAutomated(headers, "alice@example.com", null));
    }

    [Theory]
    [InlineData("delivery-status", true)]
    [InlineData("Delivery-Status", true)]
    [InlineData("disposition-notification", true)]
    [InlineData("feedback-report", true)]
    [InlineData("something-else", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_multipart_report_is_automated_only_for_machine_report_types(string? reportType, bool expected)
    {
        var shape = new ContentShape("multipart", "report", ReportType: reportType);

        Assert.Equal(expected, MessageSignals.IsAutomatedReport(shape));
        Assert.Equal(expected, MessageSignals.IsAutomated(NoHeaders, "alice@example.com", shape));
    }

    [Fact]
    public void A_report_type_on_another_media_type_does_not_count()
    {
        Assert.False(MessageSignals.IsAutomatedReport(new ContentShape("multipart", "mixed", ReportType: "delivery-status")));
        Assert.False(MessageSignals.IsAutomatedReport(new ContentShape("text", "plain", ReportType: "delivery-status")));
    }

    [Theory]
    [InlineData("noreply@example.com", true)]
    [InlineData("No-Reply <no-reply@example.com>", true)]
    [InlineData("\"Service\" <do-not-reply@example.com>", true)]
    [InlineData("donotreply@example.com", true)]
    [InlineData("do_not_reply@example.com", true)]
    [InlineData("Mail Delivery <MAILER-DAEMON@example.com>", true)]
    [InlineData("postmaster@example.com", true)]
    [InlineData("bounce@example.com", true)]
    [InlineData("bounces@example.com", true)]
    [InlineData("bounces+12345@example.com", true)]
    [InlineData("bounce-abc-123@example.com", true)]
    [InlineData("noreply+tag@example.com", true)]
    [InlineData("noreply-alerts@example.com", true)]
    [InlineData("no.reply@example.com", true)]
    [InlineData("\"noreply\"@example.com", true)]
    [InlineData("alice@example.com", false)]
    [InlineData("bouncer@example.com", false)]
    [InlineData("noreplyfan@example.com", false)]
    [InlineData("postmasterly@example.com", false)]
    [InlineData("reply@example.com", false)]
    [InlineData("Alice <alice@noreply.example.com>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not an address", false)]
    public void The_robot_sender_rule_looks_at_the_local_part_only(string? from, bool expected)
    {
        Assert.Equal(expected, MessageSignals.IsRobotSender(from));
    }

    [Theory]
    [InlineData("\"noreply@evil.example\" <alice@example.com>")]
    [InlineData("\"x <noreply@evil.example>\" <alice@example.com>")]
    [InlineData("\"a\\\" <noreply@evil.example> \" <alice@example.com>")]
    [InlineData("noreply (comment) <alice@example.com>")]
    public void A_robot_looking_display_name_does_not_make_a_human_sender_a_robot(string from)
    {
        Assert.False(MessageSignals.IsRobotSender(from));
    }

    [Fact]
    public void Only_the_first_address_of_a_from_list_is_judged()
    {
        Assert.False(MessageSignals.IsRobotSender("alice@example.com, noreply@example.com"));
        Assert.True(MessageSignals.IsRobotSender("noreply@example.com, alice@example.com"));
    }

    [Fact]
    public void An_unterminated_angle_bracket_is_not_an_address()
    {
        Assert.False(MessageSignals.IsRobotSender("noreply <alice@example.com"));
        Assert.Null(MessageSignals.FirstLocalPart("\"unterminated <noreply@example.com>"));
    }

    [Fact]
    public void Unknown_headers_with_no_other_evidence_stay_unknown()
    {
        Assert.Null(MessageSignals.IsAutomated(null, "alice@example.com", null));
        Assert.Null(MessageSignals.IsAutomated(null, null, new ContentShape("text", "plain")));
    }

    [Fact]
    public void Known_headers_with_no_evidence_mean_not_automated()
    {
        Assert.False(MessageSignals.IsAutomated(NoHeaders, "alice@example.com", new ContentShape("text", "plain")));
    }

    [Fact]
    public void Positive_evidence_wins_even_when_the_headers_are_unknown()
    {
        Assert.True(MessageSignals.IsAutomated(null, "noreply@example.com", null));
        Assert.True(MessageSignals.IsAutomated(null, "alice@example.com", new ContentShape("multipart", "report", ReportType: "delivery-status")));
    }

    [Theory]
    [InlineData("multipart", "encrypted", "application/pgp-encrypted", null, true)]
    [InlineData("multipart", "encrypted", null, null, true)]
    [InlineData("MULTIPART", "Encrypted", "application/pgp-encrypted", null, true)]
    [InlineData("application", "pkcs7-mime", null, "enveloped-data", true)]
    [InlineData("application", "x-pkcs7-mime", null, "enveloped-data", true)]
    [InlineData("application", "pkcs7-mime", null, "authEnveloped-data", true)]
    [InlineData("application", "pkcs7-mime", null, "AUTHENVELOPED-DATA", true)]
    [InlineData("application", "pkcs7-mime", null, " enveloped-data ", true)]
    [InlineData("application", "pkcs7-mime", null, "signed-data", false)]
    [InlineData("application", "pkcs7-mime", null, "certs-only", false)]
    [InlineData("application", "pkcs7-mime", null, "compressed-data", false)]
    [InlineData("application", "pkcs7-mime", null, null, false)]
    [InlineData("application", "pkcs7-signature", null, "enveloped-data", false)]
    [InlineData("application", "octet-stream", null, "enveloped-data", false)]
    [InlineData("multipart", "signed", "application/pgp-signature", null, false)]
    [InlineData("multipart", "mixed", null, null, false)]
    [InlineData("text", "plain", null, null, false)]
    public void Encryption_follows_the_rfc_3156_and_rfc_8551_shapes(
        string type, string subtype, string? protocol, string? smime, bool expected)
    {
        Assert.Equal(expected, MessageSignals.IsEncrypted(new ContentShape(type, subtype, protocol, smime)));
    }

    [Theory]
    [InlineData("-----BEGIN PGP MESSAGE-----\nabc\n-----END PGP MESSAGE-----", true)]
    [InlineData("hello\n\n-----BEGIN PGP MESSAGE-----\r\nabc\r\n-----END PGP MESSAGE-----\r\n", true)]
    [InlineData("-----BEGIN PGP MESSAGE, PART 1/2-----\nabc\n-----END PGP MESSAGE, PART 1/2-----", true)]
    [InlineData("-----BEGIN PGP MESSAGE-----\nabc\n-----END PGP MESSAGEX", false)]
    [InlineData("-----BEGIN PGP MESSAGE-----\nabc\n", false)]
    [InlineData("-----END PGP MESSAGE-----\n-----BEGIN PGP MESSAGE-----\nabc", false)]
    [InlineData("> -----BEGIN PGP MESSAGE-----\n> abc\n> -----END PGP MESSAGE-----", false)]
    [InlineData("see -----BEGIN PGP MESSAGE-----\nabc\n-----END PGP MESSAGE-----", false)]
    [InlineData("-----BEGIN PGP MESSAGE-----\nabc\nfoo -----END PGP MESSAGE-----", false)]
    [InlineData("-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\ntext\n-----BEGIN PGP SIGNATURE-----\nabc\n-----END PGP SIGNATURE-----", false)]
    [InlineData("-----BEGIN PGP PUBLIC KEY BLOCK-----\nabc\n-----END PGP PUBLIC KEY BLOCK-----", false)]
    [InlineData("-----BEGIN PGP MESSAGEX-----\nabc\n-----END PGP MESSAGE-----", false)]
    [InlineData("", false)]
    [InlineData("-----BEGIN PGP MESSAGE", false)]
    public void Inline_armor_needs_a_begin_and_an_end_line(string text, bool expected)
    {
        Assert.Equal(expected, MessageSignals.ContainsPgpArmor(text));
    }

    [Fact]
    public void A_later_valid_block_is_found_after_an_earlier_quoted_one()
    {
        const string text = "> -----BEGIN PGP MESSAGE-----\n> x\n> -----END PGP MESSAGE-----\n\n"
            + "-----BEGIN PGP MESSAGE-----\nabc\n-----END PGP MESSAGE-----\n";

        Assert.True(MessageSignals.ContainsPgpArmor(text));
    }

    [Fact]
    public void A_megabyte_of_repeated_begin_lines_is_scanned_in_linear_time()
    {
        var text = string.Concat(Enumerable.Repeat("-----BEGIN PGP MESSAGE-----\n", 40_000));
        var stopwatch = Stopwatch.StartNew();

        Assert.False(MessageSignals.ContainsPgpArmor(text));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void A_megabyte_of_end_lines_after_one_begin_is_scanned_in_linear_time()
    {
        var text = "-----BEGIN PGP MESSAGE-----\n" + string.Concat(Enumerable.Repeat("x-----END PGP MESSAGE-----\n", 40_000));
        var stopwatch = Stopwatch.StartNew();

        Assert.False(MessageSignals.ContainsPgpArmor(text));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    private static SignalHeaders Collect(params (string Field, string Value)[] pairs)
    {
        var collector = new SignalHeaderCollector();
        foreach (var (field, value) in pairs) collector.Add(field, value);
        return collector.Build();
    }
}
