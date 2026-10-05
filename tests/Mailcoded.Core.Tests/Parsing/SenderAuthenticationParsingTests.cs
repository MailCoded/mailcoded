using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class SenderAuthenticationParsingTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    private static AccountConfig Account(params string[] trusted) =>
        StoreSeed.AccountConfigFor("tester@example.com") with { TrustedAuthservIds = trusted };

    private static ParsedMessage Parse(string fixture) =>
        MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("046-auth-results-pass.eml", "mx.example.net", DmarcResult.Pass)]
    [InlineData("046-auth-results-pass.eml", "mx.other.example.net", DmarcResult.Unknown)]
    [InlineData("047-auth-results-forged-below.eml", "mx.example.net", DmarcResult.Fail)]
    [InlineData("047-auth-results-forged-below.eml", "mail.example.org", DmarcResult.Pass)]
    [InlineData("048-auth-results-self-declared.eml", "mx.example.net", DmarcResult.Unknown)]
    [InlineData("048-auth-results-self-declared.eml", "mx.google.com", DmarcResult.Pass)]
    [InlineData("038-list-mail.eml", "mx.example.net", DmarcResult.Unknown)]
    [InlineData("001-plain-simple.eml", "mx.example.net", DmarcResult.Unknown)]
    public void Fixtures_resolve_to_the_expected_result_for_an_account_that_trusts_one_id(string fixture, string trusted, DmarcResult expected)
    {
        var parsed = Parse(fixture);

        Assert.Equal(expected, ParsedSignals.ValuesOf(parsed, Account(trusted)).Dmarc);
    }

    [Fact]
    public void The_mx_example_org_instance_of_a_list_mail_is_read_only_by_an_account_that_trusts_it()
    {
        var parsed = Parse("038-list-mail.eml");

        Assert.Equal(DmarcResult.Pass, ParsedSignals.ValuesOf(parsed, Account("mx.example.org")).Dmarc);
        Assert.Equal(DmarcResult.Unknown, ParsedSignals.ValuesOf(parsed, Account("relay.example.net")).Dmarc);
    }

    [Fact]
    public void An_account_with_no_trusted_id_gets_no_evaluation_at_all()
    {
        var parsed = Parse("046-auth-results-pass.eml");

        Assert.Null(ParsedSignals.ValuesOf(parsed, Account()).Dmarc);
        Assert.Null(ParsedSignals.ValuesOf(parsed, null).Dmarc);
    }

    [Fact]
    public void A_gmail_account_trusts_the_google_id_without_any_configuration()
    {
        var gmail = Account() with { Imap = new ImapConfig { Host = "imap.gmail.com" } };

        Assert.Equal(DmarcResult.Pass, ParsedSignals.ValuesOf(Parse("048-auth-results-self-declared.eml"), gmail).Dmarc);
        Assert.Equal(DmarcResult.Unknown, ParsedSignals.ValuesOf(Parse("046-auth-results-pass.eml"), gmail).Dmarc);
    }

    [Fact]
    public void Imported_files_are_never_verified_whatever_they_claim()
    {
        var values = ParsedSignals.ImportedValuesOf(Parse("046-auth-results-pass.eml"));

        Assert.Equal(DmarcResult.Imported, values.Dmarc);
    }

    [Fact]
    public void The_forged_fixtures_keep_every_instance_for_the_decision()
    {
        var headers = Require.Ref(Parse("047-auth-results-forged-below.eml").SignalHeaders, "the signal headers");

        Assert.Equal(3, headers.AuthenticationResults.Count);
        Assert.Contains("dmarc=fail", headers.AuthenticationResults[0], StringComparison.Ordinal);
        Assert.Contains("dmarc=pass", headers.AuthenticationResults[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Both_parse_entry_points_agree_on_the_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = Account("mx.example.net");

        foreach (var fixture in new[] { "046-auth-results-pass.eml", "047-auth-results-forged-below.eml", "048-auth-results-self-declared.eml" })
        {
            var fromBytes = MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, ct);

            using var stream = Fixtures.Open(fixture);
            var hashed = MessageParser.Default.ParseAndHash(stream, InternalDate, ct).Message;

            Assert.Equal(
                ParsedSignals.ValuesOf(fromBytes, account).Dmarc,
                ParsedSignals.ValuesOf(hashed, account).Dmarc);
        }
    }

    [Theory]
    [InlineData("From: \"Sender, A\" <sender@example.com>", DmarcResult.Pass)]
    [InlineData("From: sender@example.com, evil@example.org", DmarcResult.Unknown)]
    [InlineData("From: sender@example.org", DmarcResult.Unknown)]
    [InlineData("From: =?utf-8?q?sender=40example=2Eorg?= <sender@example.com>", DmarcResult.Pass)]
    [InlineData("Sender: sender@example.com", DmarcResult.Unknown)]
    public void The_from_header_decides_which_domain_a_result_may_verify(string fromLine, DmarcResult expected)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(
            "Authentication-Results: mx.example.net; dmarc=pass header.from=example.com\r\n"
            + fromLine + "\r\nTo: b@example.org\r\nSubject: x\r\nMIME-Version: 1.0\r\nContent-Type: text/plain\r\n\r\nbody\r\n");

        var parsed = MessageParser.Default.Parse(raw, InternalDate, TestContext.Current.CancellationToken);

        Assert.Equal(expected, ParsedSignals.ValuesOf(parsed, Account("mx.example.net")).Dmarc);
    }
}
