using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class ListUnsubscribeParsingTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    private static UnsubscribeOptions Options(string fixture)
    {
        var parsed = MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, TestContext.Current.CancellationToken);
        return Require.Ref(UnsubscribeStorage.Decode(UnsubscribeStorage.Encode(parsed.SignalHeaders)), "options");
    }

    [Fact]
    public void A_list_message_offers_a_mailto_and_an_https_url_with_one_click()
    {
        var options = Options("038-list-mail.eml");

        Assert.Equal("unsubscribe@lists.example.net", options.Mailto?.Address);
        Assert.Equal("unsubscribe", options.Mailto?.Subject);
        Assert.Equal(["https://lists.example.net/unsubscribe/abc123"], options.Web);
        Assert.True(options.OneClick);
    }

    [Fact]
    public void A_commented_folded_mailto_decodes_its_subject_and_body()
    {
        var options = Options("050-unsubscribe-mailto.eml");

        Assert.Equal(
            new MailtoUnsubscribe("leave-9f3a@bounce.club.example.org", "Unsubscribe me", "Please remove this address"),
            options.Mailto);
        Assert.Empty(options.Web);
        Assert.False(options.OneClick);
    }

    [Fact]
    public void An_https_only_message_lists_both_urls_and_advertises_one_click()
    {
        var options = Options("051-unsubscribe-https-one-click.eml");

        Assert.Null(options.Mailto);
        Assert.Equal(["https://shop.example.com/u/7c1e5", "http://shop.example.com/u/7c1e5"], options.Web);
        Assert.True(options.OneClick);
    }

    [Fact]
    public void Hostile_headers_leave_only_the_one_entry_that_survives_strict_parsing()
    {
        var options = Options("052-unsubscribe-hostile.eml");

        Assert.Equal(new MailtoUnsubscribe("d@example.net", "Leave list", "bye"), options.Mailto);
        Assert.Empty(options.Web);
        Assert.False(options.OneClick);
    }

    [Theory]
    [InlineData("001-plain-simple.eml")]
    [InlineData("002-multipart-alternative.eml")]
    [InlineData("040-dsn-report.eml")]
    public void Messages_without_the_headers_offer_nothing(string fixture)
    {
        Assert.True(Options(fixture).IsEmpty);
    }

    [Fact]
    public void Both_parse_entry_points_agree()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var fixture in new[] { "038-list-mail.eml", "050-unsubscribe-mailto.eml", "052-unsubscribe-hostile.eml" })
        {
            var fromBytes = MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, ct);

            using var stream = Fixtures.Open(fixture);
            var hashed = MessageParser.Default.ParseAndHash(stream, InternalDate, ct).Message;

            Assert.Equal(
                UnsubscribeStorage.Encode(fromBytes.SignalHeaders),
                UnsubscribeStorage.Encode(hashed.SignalHeaders));
        }
    }
}
