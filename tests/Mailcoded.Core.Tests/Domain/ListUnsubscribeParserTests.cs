using System.Diagnostics;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class ListUnsubscribeParserTests
{
    private static UnsubscribeOptions Parse(string? header, string? post = null) => ListUnsubscribeParser.Parse(header, post);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>")]
    [InlineData("<   >")]
    [InlineData("mailto:leave@example.com")]
    [InlineData("https://example.com/u")]
    [InlineData("<https://example.com/u")]
    [InlineData("(<https://example.com/u>)")]
    [InlineData("(unterminated <https://example.com/u>")]
    [InlineData("<ftp://example.com/u>")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<data:text/html;base64,AAAA>")]
    [InlineData("<file:///etc/passwd>")]
    [InlineData("<tel:+15550100>")]
    public void Headers_that_offer_nothing_usable_parse_to_empty(string? header)
    {
        Assert.True(Parse(header).IsEmpty);
    }

    [Fact]
    public void A_mailto_and_an_https_url_are_both_returned()
    {
        var options = Parse("<mailto:leave@example.org?subject=unsubscribe>, <https://lists.example.org/u/abc>");

        Assert.Equal(new MailtoUnsubscribe("leave@example.org", "unsubscribe", null), options.Mailto);
        Assert.Equal(["https://lists.example.org/u/abc"], options.Web);
        Assert.False(options.OneClick);
    }

    [Theory]
    [InlineData("<https://lists.example.org/u/abc>")]
    [InlineData("<mailto:leave@example.org>,<https://lists.example.org/u/abc>")]
    [InlineData("<mailto:leave@example.org> , <https://lists.example.org/u/abc>")]
    [InlineData("(Mailing list) <https://lists.example.org/u/abc>")]
    [InlineData("<https://lists.example.org/u/abc> (via the list)")]
    [InlineData("  <  https://lists.example.org/u/abc  >  ")]
    [InlineData("<HTTPS://lists.example.org/u/abc>")]
    public void Separators_comments_and_padding_between_entries_are_tolerated(string header)
    {
        var options = Parse(header);

        Assert.Single(options.Web);
        Assert.EndsWith("lists.example.org/u/abc", options.Web[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_comment_containing_angle_brackets_does_not_start_an_entry()
    {
        var options = Parse("(see <https://evil.example.com/x> instead) <https://lists.example.org/u>");

        Assert.Equal(["https://lists.example.org/u"], options.Web);
    }

    [Fact]
    public void Nested_and_escaped_comments_are_skipped_whole()
    {
        var options = Parse(@"(a (nested <https://evil.example.com/a>) and \) <https://evil.example.com/b>) <https://lists.example.org/u>");

        Assert.Equal(["https://lists.example.org/u"], options.Web);
    }

    [Fact]
    public void The_first_valid_mailto_wins_and_a_bad_one_does_not_shadow_a_later_good_one()
    {
        var options = Parse("<mailto:a@example.net,b@example.net>, <mailto:c@example.net?subject=Leave>, <mailto:d@example.net>");

        Assert.Equal("c@example.net", options.Mailto?.Address);
        Assert.Equal("Leave", options.Mailto?.Subject);
    }

    [Fact]
    public void At_most_four_distinct_web_urls_are_kept_in_order()
    {
        var header = string.Join(", ", Enumerable.Range(1, 9).Select(i => $"<https://example.com/{i}>").Append("<https://example.com/1>"));

        Assert.Equal(
            ["https://example.com/1", "https://example.com/2", "https://example.com/3", "https://example.com/4"],
            Parse(header).Web);
    }

    [Fact]
    public void A_repeated_url_is_listed_once()
    {
        Assert.Equal(["https://example.com/u"], Parse("<https://example.com/u>, <https://example.com/u>").Web);
    }

    [Fact]
    public void Plain_http_is_listed_but_cannot_make_a_one_click_offer()
    {
        var options = Parse("<http://example.com/u>", "List-Unsubscribe=One-Click");

        Assert.Equal(["http://example.com/u"], options.Web);
        Assert.False(options.OneClick);
    }

    [Theory]
    [InlineData("List-Unsubscribe=One-Click", true)]
    [InlineData("list-unsubscribe=one-click", true)]
    [InlineData("  List-Unsubscribe=One-Click  ", true)]
    [InlineData("List-Unsubscribe=One-Click, List-Unsubscribe=One-Click", false)]
    [InlineData("List-Unsubscribe=One-Click-Please", false)]
    [InlineData("List-Unsubscribe=Manual", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void One_click_needs_the_exact_post_value_and_an_https_url(string? post, bool expected)
    {
        Assert.Equal(expected, Parse("<https://example.com/u>", post).OneClick);
    }

    [Fact]
    public void The_post_header_alone_offers_nothing()
    {
        var options = Parse(null, "List-Unsubscribe=One-Click");

        Assert.True(options.IsEmpty);
        Assert.False(options.OneClick);
    }

    [Theory]
    [InlineData("<https://example.com@evil.example.net/u>")]
    [InlineData("<https://user:pass@example.com/u>")]
    [InlineData("<https:///u>")]
    [InlineData("<https://>")]
    [InlineData("<https://example.com\\@evil.example.net/u>")]
    [InlineData("<https://exa mple.com/u>")]
    [InlineData("<https://exa%6Dple.com/u>")]
    [InlineData("<https://[::1]/u>")]
    [InlineData("<https://.example.com/u>")]
    [InlineData("<https://example..com/u>")]
    [InlineData("<https://example.com./u>")]
    [InlineData("<https://-example.com/u>")]
    [InlineData("<https://example.com:/u>")]
    [InlineData("<https://example.com:99999/u>")]
    [InlineData("<https://example.com:80a/u>")]
    [InlineData("<https://exämple.com/u>")]
    [InlineData("<https://example.com/u‮>")]
    [InlineData("<https://example.com/\"onclick=x>")]
    [InlineData("<https://example.com/u|rm>")]
    [InlineData("<https://example.com/u^>")]
    [InlineData("<https://example.com/u`>")]
    public void Hostile_or_malformed_web_urls_are_dropped(string header)
    {
        Assert.Empty(Parse(header).Web);
    }

    [Fact]
    public void A_url_over_the_length_cap_is_dropped_and_one_at_the_cap_is_kept()
    {
        var prefix = "https://example.com/";
        var atCap = prefix + new string('a', ListUnsubscribeParser.MaxUriChars - prefix.Length);
        var overCap = atCap + "a";

        Assert.Equal([atCap], Parse($"<{atCap}>").Web);
        Assert.Empty(Parse($"<{overCap}>").Web);
    }

    [Theory]
    [InlineData("mailto:leave@example.org", "leave@example.org", null, null)]
    [InlineData("MAILTO:leave@example.org", "leave@example.org", null, null)]
    [InlineData("mailto:Leave.Me@EXAMPLE.org", "Leave.Me@example.org", null, null)]
    [InlineData("mailto:leave@example.org?subject=unsubscribe", "leave@example.org", "unsubscribe", null)]
    [InlineData("mailto:leave@example.org?SUBJECT=Hi&BODY=Bye", "leave@example.org", "Hi", "Bye")]
    [InlineData("mailto:leave@example.org?subject=Please%20remove%20me", "leave@example.org", "Please remove me", null)]
    [InlineData("mailto:leave@example.org?subject=a+b", "leave@example.org", "a+b", null)]
    [InlineData("mailto:leave@example.org?body=line1%0D%0Aline2", "leave@example.org", null, "line1\nline2")]
    [InlineData("mailto:leave@example.org?body=line1%0Aline2%09tab", "leave@example.org", null, "line1\nline2\ttab")]
    [InlineData("mailto:leave@example.org?subject=caf%C3%A9", "leave@example.org", "café", null)]
    [InlineData("mailto:%6Ceave@example.org", "leave@example.org", null, null)]
    [InlineData("mailto:leave%40example.org", "leave@example.org", null, null)]
    [InlineData("mailto:leave@example.org?subject=", "leave@example.org", null, null)]
    [InlineData("mailto:leave@example.org?subject", "leave@example.org", null, null)]
    [InlineData("mailto:leave@example.org?", "leave@example.org", null, null)]
    [InlineData("mailto:leave@example.org?subject=First&subject=Second", "leave@example.org", "First", null)]
    [InlineData("mailto:leave@example.org?%73ubject=Encoded%20name", "leave@example.org", "Encoded name", null)]
    [InlineData("mailto:leave+list-1@example.org", "leave+list-1@example.org", null, null)]
    [InlineData("mailto:unsub-9f3a@bounce.lists.example.org?subject=unsubscribe", "unsub-9f3a@bounce.lists.example.org", "unsubscribe", null)]
    public void A_well_formed_mailto_yields_one_address_and_the_two_honoured_fields(
        string uri, string address, string? subject, string? body)
    {
        var options = Parse($"<{uri}>");

        Assert.Equal(new MailtoUnsubscribe(address, subject, body), options.Mailto);
    }

    [Theory]
    [InlineData("mailto:leave@example.org?cc=a@example.net&subject=Hi")]
    [InlineData("mailto:leave@example.org?bcc=a@example.net&subject=Hi")]
    [InlineData("mailto:leave@example.org?to=a@example.net&subject=Hi")]
    [InlineData("mailto:leave@example.org?from=a@example.net&subject=Hi")]
    [InlineData("mailto:leave@example.org?reply-to=a@example.net&subject=Hi")]
    [InlineData("mailto:leave@example.org?in-reply-to=%3Cx@example.net%3E&subject=Hi")]
    [InlineData("mailto:leave@example.org?x-custom=1&Bcc=a@example.net&subject=Hi")]
    [InlineData("mailto:leave@example.org?subject=Hi&%62cc=a@example.net")]
    [InlineData("mailto:leave@example.org?subject=Hi&cc=%ZZ")]
    public void Other_hfields_are_ignored_and_never_become_recipients(string uri)
    {
        var options = Parse($"<{uri}>");

        Assert.Equal(new MailtoUnsubscribe("leave@example.org", "Hi", null), options.Mailto);
    }

    [Theory]
    [InlineData("mailto:")]
    [InlineData("mailto:?to=a@example.net")]
    [InlineData("mailto:?subject=Hi")]
    [InlineData("mailto:a@example.net,b@example.net")]
    [InlineData("mailto:a@example.net%2Cb@example.net")]
    [InlineData("mailto:a@example.net;b@example.net")]
    [InlineData("mailto:a@example.net%3Bb@example.net")]
    [InlineData("mailto:a@example.net%0D%0ABcc:x@example.net")]
    [InlineData("mailto:a@example.net%0Ax@example.net")]
    [InlineData("mailto:a%0D%0A@example.net")]
    [InlineData("mailto:a@@example.net")]
    [InlineData("mailto:a@b@example.net")]
    [InlineData("mailto:@example.net")]
    [InlineData("mailto:a@")]
    [InlineData("mailto:a@example")]
    [InlineData("mailto:a@example..net")]
    [InlineData("mailto:a@-example.net")]
    [InlineData("mailto:a@[127.0.0.1]")]
    [InlineData("mailto:\"a b\"@example.net")]
    [InlineData("mailto:a%20b@example.net")]
    [InlineData("mailto:a<b@example.net")]
    [InlineData("mailto:.a@example.net")]
    [InlineData("mailto:a.@example.net")]
    [InlineData("mailto:a..b@example.net")]
    [InlineData("mailto:A%00@example.net")]
    [InlineData("mailto:a%@example.net")]
    [InlineData("mailto:a%4@example.net")]
    [InlineData("mailto:a%GG@example.net")]
    [InlineData("mailto:a@example.net%")]
    [InlineData("mailto:a@example.net#frag")]
    [InlineData("mailto:a@example.net?subject=x#frag")]
    [InlineData("mailto:Ada <a@example.net>")]
    [InlineData("mailto:a@example.net?subject=%FF")]
    [InlineData("mailto:a@example.net?subject=%C3")]
    [InlineData("mailto:a@example.net?subject=%C0%80")]
    [InlineData("mailto:a@example.net?subject=%ED%A0%80")]
    [InlineData("mailto:a@example.net?body=%FF")]
    [InlineData("mailto:a@example.net?subject=%")]
    [InlineData("mailto:a@example.net?subject=Hi%0D%0ABcc:x@example.net")]
    [InlineData("mailto:a@example.net?subject=Hi%0ABcc:x@example.net")]
    [InlineData("mailto:a@example.net?subject=Hi%0DBcc:x@example.net")]
    [InlineData("mailto:a@example.net?subject=Hi%00")]
    [InlineData("mailto:a@example.net?subject=Hi%09tab")]
    [InlineData("mailto:a@example.net?subject=Hi%1B%5B2J")]
    [InlineData("mailto:a@example.net?subject=Hi%7F")]
    [InlineData("mailto:a@example.net?subject=Hi%C2%85next")]
    [InlineData("mailto:a@example.net?subject=Hi%E2%80%A8sep")]
    [InlineData("mailto:a@example.net?subject=Hi%E2%80%AEevil")]
    [InlineData("mailto:a@example.net?subject=Hi%E2%80%8Bzw")]
    [InlineData("mailto:a@example.net?body=Hi%0D")]
    [InlineData("mailto:a@example.net?body=Hi%00")]
    [InlineData("mailto:a@example.net?body=Hi%1B%5B31m")]
    [InlineData("mailto:a@example.net?body=Hi%C2%85")]
    [InlineData("mailto:a@example.net?body=%E2%80%AEx")]
    public void A_malformed_or_hostile_mailto_is_rejected_whole(string uri)
    {
        Assert.Null(Parse($"<{uri}>").Mailto);
        Assert.False(ListUnsubscribeParser.TryParseMailto(uri, out _));
    }

    [Fact]
    public void Mailto_lengths_are_capped_at_the_boundary()
    {
        var subjectAtCap = new string('s', ListUnsubscribeParser.MaxSubjectChars);
        var bodyAtCap = new string('b', ListUnsubscribeParser.MaxBodyChars);

        Assert.True(ListUnsubscribeParser.TryParseMailto($"mailto:a@example.net?subject={subjectAtCap}", out var subject));
        Assert.Equal(subjectAtCap, subject.Subject);
        Assert.False(ListUnsubscribeParser.TryParseMailto($"mailto:a@example.net?subject={subjectAtCap}s", out _));

        Assert.True(ListUnsubscribeParser.TryParseMailto($"mailto:a@example.net?body={bodyAtCap}", out var body));
        Assert.Equal(bodyAtCap, body.Body);
        Assert.False(ListUnsubscribeParser.TryParseMailto($"mailto:a@example.net?body={bodyAtCap}b", out _));
    }

    [Fact]
    public void A_whole_mailto_over_the_uri_cap_is_rejected()
    {
        var local = new string('a', 60);
        var padding = new string('x', ListUnsubscribeParser.MaxUriChars);

        Assert.False(ListUnsubscribeParser.TryParseMailto($"mailto:{local}@example.net?x-pad={padding}", out _));
    }

    [Fact]
    public void An_address_over_the_local_part_limit_is_rejected()
    {
        Assert.False(ListUnsubscribeParser.TryParseMailto($"mailto:{new string('a', 65)}@example.net", out _));
        Assert.True(ListUnsubscribeParser.TryParseMailto($"mailto:{new string('a', 64)}@example.net", out _));
    }

    [Fact]
    public void An_internationalised_address_in_utf8_percent_encoding_is_accepted()
    {
        Assert.True(ListUnsubscribeParser.TryParseMailto("mailto:%C3%A9a@example.net", out var result));
        Assert.Equal("éa@example.net", result.Address);
    }

    [Fact]
    public void Non_ascii_bytes_in_the_raw_uri_reject_it()
    {
        Assert.False(ListUnsubscribeParser.TryParseMailto("mailto:éa@example.net", out _));
        Assert.False(ListUnsubscribeParser.TryParseMailto("mailto:a@example.net?subject=café", out _));
        Assert.Empty(Parse("<https://example.com/café>").Web);
    }

    [Fact]
    public void A_folded_header_value_parses_like_the_unfolded_one()
    {
        var folded = "<mailto:leave@example.org?subject=unsubscribe>,\r\n <https://lists.example.org/u/abc>";
        var collector = new SignalHeaderCollector();
        collector.Add("List-Unsubscribe", folded);
        var headers = Require.Ref(collector.Build().ListUnsubscribe, "the header");

        var options = Parse(headers);

        Assert.Equal("leave@example.org", options.Mailto?.Address);
        Assert.Equal(["https://lists.example.org/u/abc"], options.Web);
    }

    [Fact]
    public void A_url_split_by_folding_whitespace_is_rejected_not_repaired()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("List-Unsubscribe", "<https://lists.example.org/u/\r\n abc>");

        Assert.Empty(Parse(collector.Build().ListUnsubscribe).Web);
    }

    [Fact]
    public void A_header_truncated_by_the_collector_cap_keeps_only_whole_entries()
    {
        var first = "<https://lists.example.org/u/abc>";
        var padding = string.Concat(Enumerable.Repeat(", <https://pad.example.org/x>", 200));
        var collector = new SignalHeaderCollector();
        collector.Add("List-Unsubscribe", first + padding);

        var options = Parse(collector.Build().ListUnsubscribe);

        Assert.Equal("https://lists.example.org/u/abc", options.Web[0]);
        Assert.True(options.Web.Count <= ListUnsubscribeParser.MaxWebUrls);
    }

    [Fact]
    public void Garbage_between_entries_is_skipped()
    {
        var options = Parse("junk ; \u0000 ,,, <https://example.com/u> ;;; <mailto:leave@example.org> trailing");

        Assert.Equal(["https://example.com/u"], options.Web);
        Assert.Equal("leave@example.org", options.Mailto?.Address);
    }

    [Fact]
    public void Pathological_inputs_finish_in_linear_time_and_bounded_output()
    {
        var inputs = new[]
        {
            new string('<', 200_000),
            string.Concat(Enumerable.Repeat("<a>", 100_000)),
            string.Concat(Enumerable.Repeat("(", 200_000)),
            new string('(', 100_000) + "<https://example.com/u>" + new string(')', 100_000),
            "<mailto:a@example.net?" + string.Concat(Enumerable.Repeat("x=%", 100_000)) + ">",
            "<mailto:a@example.net?" + string.Concat(Enumerable.Repeat("&", 100_000)) + "subject=Hi>",
            "<https://example.com/" + new string('a', 500_000) + ">",
            string.Concat(Enumerable.Repeat("<https://example.com/u>,", 50_000)),
        };

        var watch = Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            var options = Parse(input);
            Assert.True(options.Web.Count <= ListUnsubscribeParser.MaxWebUrls);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Entries_past_the_examination_cap_are_not_read()
    {
        var filler = string.Concat(Enumerable.Repeat("<ftp://example.com/x>,", ListUnsubscribeParser.MaxEntries));

        Assert.True(Parse(filler + "<https://example.com/late>").IsEmpty);
    }

    [Fact]
    public void Storage_decodes_unknown_and_none_distinctly()
    {
        Assert.Null(UnsubscribeStorage.Decode(null));
        Assert.True(Require.Ref(UnsubscribeStorage.Decode(string.Empty), "decoded").IsEmpty);
    }

    [Fact]
    public void Storage_encodes_unknown_for_absent_headers_and_empty_for_none()
    {
        Assert.Null(UnsubscribeStorage.Encode(null));
        Assert.Equal(string.Empty, UnsubscribeStorage.Encode(SignalHeaders.None));
        Assert.Equal(string.Empty, UnsubscribeStorage.Encode(new SignalHeaders { ListUnsubscribePost = "List-Unsubscribe=One-Click" }));
        Assert.Equal(string.Empty, UnsubscribeStorage.Encode(new SignalHeaders { ListUnsubscribe = "  " }));
    }

    [Fact]
    public void Storage_round_trips_the_header_and_the_post_value()
    {
        var headers = new SignalHeaders
        {
            ListUnsubscribe = "<mailto:leave@example.org?subject=unsubscribe>, <https://lists.example.org/u/abc>",
            ListUnsubscribePost = "List-Unsubscribe=One-Click",
        };

        var stored = Require.Ref(UnsubscribeStorage.Encode(headers), "stored");
        var options = Require.Ref(UnsubscribeStorage.Decode(stored), "options");

        Assert.Equal("leave@example.org", options.Mailto?.Address);
        Assert.Equal(["https://lists.example.org/u/abc"], options.Web);
        Assert.True(options.OneClick);
    }

    [Fact]
    public void A_header_value_cannot_forge_the_post_part_because_line_feeds_never_survive_collection()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("List-Unsubscribe", "<https://example.com/u>\nList-Unsubscribe=One-Click");
        var headers = collector.Build();

        var options = Require.Ref(UnsubscribeStorage.Decode(UnsubscribeStorage.Encode(headers)), "options");

        Assert.False(options.OneClick);
    }
}
