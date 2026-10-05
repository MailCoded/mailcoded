using Mailcoded.Core.Domain.Signals;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>Every row is something a sender can put in a header. The only way to a pass is a trusted server saying so for the From domain.</summary>
public sealed class SenderAuthenticationTests
{
    private const string From = "Sender Example <sender@example.com>";
    private static readonly AuthservTrust Trust = AuthservTrust.From(["mx.example.net"]);

    private static DmarcResult Evaluate(string[] instances, string? from = From, AuthservTrust? trust = null) =>
        SenderAuthentication.Evaluate(instances, from, trust ?? Trust);

    [Theory]
    [InlineData("mx.example.net; dmarc=pass header.from=example.com", DmarcResult.Pass)]
    [InlineData("mx.example.net; dmarc=fail header.from=example.com", DmarcResult.Fail)]
    [InlineData("mx.example.net; dmarc=none header.from=example.com", DmarcResult.None)]
    [InlineData("mx.example.net; dmarc=temperror header.from=example.com", DmarcResult.Unknown)]
    [InlineData("mx.example.net; dmarc=permerror header.from=example.com", DmarcResult.Unknown)]
    [InlineData("mx.example.net; dmarc=policy header.from=example.com", DmarcResult.Unknown)]
    [InlineData("mx.example.net; dmarc=bestguesspass header.from=example.com", DmarcResult.Unknown)]
    [InlineData("MX.EXAMPLE.NET; DMARC=PASS HEADER.FROM=EXAMPLE.COM", DmarcResult.Pass)]
    [InlineData("mx.example.net.; dmarc=pass header.from=example.com.", DmarcResult.Pass)]
    [InlineData("mx.example.net 1; dmarc=pass header.from=example.com", DmarcResult.Pass)]
    [InlineData("mx.example.net; dmarc/1=pass header.from=example.com", DmarcResult.Pass)]
    [InlineData("mx.example.net;\r\n\tdmarc = pass (p=NONE sp=NONE dis=NONE)\r\n\theader.from = example.com", DmarcResult.Pass)]
    [InlineData("\"mx.example.net\"; dmarc=pass header.from=example.com", DmarcResult.Pass)]
    [InlineData("mx.example.net; dkim=pass header.d=example.com; spf=pass smtp.mailfrom=sender@example.com; dmarc=pass header.from=example.com; arc=none", DmarcResult.Pass)]
    public void One_trusted_instance_is_read_for_the_dmarc_result(string instance, DmarcResult expected)
    {
        Assert.Equal(expected, Evaluate([instance]));
    }

    [Theory]
    [InlineData("mx.example.net; dkim=pass header.d=example.com header.i=@example.com")]
    [InlineData("mx.example.net; dkim=pass header.d=example.com; spf=pass smtp.mailfrom=sender@example.com")]
    [InlineData("mx.example.net; none")]
    [InlineData("mx.example.net")]
    [InlineData("mx.example.net; dmarc=pass")]
    [InlineData("mx.example.net; dmarc=pass header.from=other.example.org")]
    [InlineData("mx.example.net; dmarc=pass header.from=sub.example.com")]
    [InlineData("mx.example.net; dmarc=pass header.from=example.com.evil.example.org")]
    [InlineData("mx.example.net; dmarc/2=pass header.from=example.com")]
    [InlineData("mx.example.net; xdmarc=pass header.from=example.com")]
    [InlineData("mx.example.net; dmarc=pass@example.com header.from=example.com")]
    [InlineData("mx.example.net; dmarc=\"pass\" header.from=example.com")]
    [InlineData("mx.example.net; dmarc=pass header.from=\"\"")]
    [InlineData("mx.example.net; dmarc=pass body.from=example.com")]
    [InlineData("mx.example.net; dmarc=pass smtp.from=example.com")]
    [InlineData("mx.example.net; dmarc=pass header.from=evil.example.org header.from=example.com")]
    [InlineData("mx.example.net; dmarc=pass header.from=example.com header.from=evil.example.org")]
    public void Anything_short_of_a_dmarc_result_that_names_the_from_domain_is_unknown(string instance)
    {
        Assert.Equal(DmarcResult.Unknown, Evaluate([instance]));
    }

    [Theory]
    [InlineData("mx.example.net (dmarc=pass header.from=example.com)")]
    [InlineData("mx.example.net; spf=none (; dmarc=pass header.from=example.com)")]
    [InlineData("mx.example.net; spf=none (dmarc=pass; header.from=example.com)")]
    [InlineData("mx.example.net; spf=fail smtp.mailfrom=\"x; dmarc=pass header.from=example.com\"")]
    [InlineData("mx.example.net; spf=fail smtp.mailfrom=\"x\\\"; dmarc=pass header.from=example.com; \"")]
    [InlineData("mx.example.net; dkim=pass header.d=example.com (dmarc=pass header.from=example.com)")]
    [InlineData("mx.example.net; dkim=pass reason=\"dmarc=pass; header.from=example.com\"")]
    [InlineData("mx.example.net; spf=fail dmarc=pass header.from=example.com")]
    [InlineData("mx.example.net; spf=fail (comment (nested)) x=\"y\" dmarc=pass header.from=example.com")]
    public void A_pass_that_sits_in_a_comment_a_quoted_string_or_another_result_does_not_count(string instance)
    {
        Assert.Equal(DmarcResult.Unknown, Evaluate([instance]));
    }

    [Theory]
    [InlineData("mx.example.net; dmarc=fail (dmarc=pass) header.from=example.com", DmarcResult.Fail)]
    [InlineData("mx.example.net; dmarc=fail header.from=example.com; spf=pass smtp.mailfrom=\"a; dmarc=pass\"", DmarcResult.Fail)]
    [InlineData("mx.example.net; dmarc=none header.from=example.com reason=\"dmarc=pass\"", DmarcResult.None)]
    public void Forged_text_next_to_a_real_result_does_not_change_it(string instance, DmarcResult expected)
    {
        Assert.Equal(expected, Evaluate([instance]));
    }

    [Fact]
    public void Nothing_is_trusted_unless_the_account_names_an_authserv_id()
    {
        var instances = new[] { "mx.example.net; dmarc=pass header.from=example.com" };

        Assert.Equal(DmarcResult.Unknown, Evaluate(instances, trust: AuthservTrust.None));
        Assert.Null(SenderAuthentication.Dmarc(new SignalHeaders { AuthenticationResults = instances }, From, AuthservTrust.None));
    }

    [Theory]
    [InlineData("mx.example.org; dmarc=pass header.from=example.com")]
    [InlineData("mx.example.net.evil.example.org; dmarc=pass header.from=example.com")]
    [InlineData("evil.mx.example.net; dmarc=pass header.from=example.com")]
    [InlineData("mx.google.com; dmarc=pass header.from=example.com")]
    [InlineData("evil.example.org (mx.example.net); dmarc=pass header.from=example.com")]
    [InlineData("example.net; dmarc=pass header.from=example.com")]
    [InlineData("dmarc=pass header.from=example.com")]
    public void An_instance_from_an_authserv_id_the_account_does_not_trust_is_ignored(string instance)
    {
        Assert.Equal(DmarcResult.Unknown, Evaluate([instance]));
    }

    [Fact]
    public void A_header_the_server_did_not_write_but_the_sender_did_cannot_name_its_way_to_a_pass()
    {
        var google = AuthservTrust.From(["mx.google.com"]);
        var planted = new[] { "mx.example.net; dmarc=pass header.from=example.com" };

        Assert.Equal(DmarcResult.Unknown, Evaluate(planted, trust: google));
    }

    [Fact]
    public void The_topmost_trusted_instance_decides_and_a_lower_pass_planted_by_the_sender_is_never_consulted()
    {
        var topmostFails = new[]
        {
            "mx.example.net; dmarc=fail header.from=example.com",
            "mx.example.net; dmarc=pass header.from=example.com",
            "mail.example.org; dmarc=pass header.from=example.com",
        };

        Assert.Equal(DmarcResult.Fail, Evaluate(topmostFails));
    }

    [Fact]
    public void A_topmost_trusted_instance_with_no_dmarc_result_does_not_fall_through_to_a_lower_one()
    {
        var instances = new[]
        {
            "mx.example.net; spf=pass smtp.mailfrom=sender@example.com",
            "mx.example.net; dmarc=pass header.from=example.com",
        };

        Assert.Equal(DmarcResult.Unknown, Evaluate(instances));
    }

    [Fact]
    public void Untrusted_instances_above_the_trusted_one_are_skipped()
    {
        var passes = new[]
        {
            "relay.example.org; dmarc=fail header.from=example.com",
            "mx.example.net; dmarc=pass header.from=example.com",
        };

        Assert.Equal(DmarcResult.Pass, Evaluate(passes));

        var fails = new[]
        {
            "relay.example.org; dmarc=pass header.from=example.com",
            "mx.example.net; dmarc=fail header.from=example.com",
        };

        Assert.Equal(DmarcResult.Fail, Evaluate(fails));
    }

    [Fact]
    public void An_instance_that_cannot_be_read_above_a_trusted_one_stops_the_search_rather_than_being_stepped_over()
    {
        var instances = new[]
        {
            "\"unterminated; dmarc=fail",
            "mx.example.net; dmarc=pass header.from=example.com",
        };

        Assert.Equal(DmarcResult.Unknown, Evaluate(instances));
        Assert.Equal(DmarcResult.Unknown, Evaluate(["", "mx.example.net; dmarc=pass header.from=example.com"]));
    }

    [Fact]
    public void A_trusted_instance_that_is_malformed_after_its_authserv_id_is_unknown_not_skipped()
    {
        var instances = new[]
        {
            "mx.example.net; dmarc=fail header.from=example.com (never closed",
            "mx.example.net; dmarc=pass header.from=example.com",
        };

        Assert.Equal(DmarcResult.Unknown, Evaluate(instances));
    }

    [Fact]
    public void Contradicting_results_for_the_from_domain_are_unknown_and_agreeing_ones_are_not()
    {
        Assert.Equal(
            DmarcResult.Unknown,
            Evaluate(["mx.example.net; dmarc=pass header.from=example.com; dmarc=fail header.from=example.com"]));

        Assert.Equal(
            DmarcResult.Unknown,
            Evaluate(["mx.example.net; dmarc=pass header.from=example.com; dmarc=temperror header.from=example.com"]));

        Assert.Equal(
            DmarcResult.Pass,
            Evaluate(["mx.example.net; dmarc=pass header.from=example.com; dmarc=pass header.from=example.com"]));
    }

    [Fact]
    public void A_result_for_another_domain_is_ignored_when_one_names_the_from_domain()
    {
        var instance = "mx.example.net; dmarc=fail header.from=other.example.org; dmarc=pass header.from=example.com";

        Assert.Equal(DmarcResult.Pass, Evaluate([instance]));
    }

    [Fact]
    public void Dkim_and_spf_passes_alone_never_verify()
    {
        var instance = "mx.example.net; dkim=pass header.d=example.com header.i=@example.com; spf=pass smtp.mailfrom=sender@example.com";

        Assert.Equal(DmarcResult.Unknown, Evaluate([instance]));
    }

    [Theory]
    [InlineData("sender@example.com", DmarcResult.Pass)]
    [InlineData("<sender@example.com>", DmarcResult.Pass)]
    [InlineData("Sender <SENDER@EXAMPLE.COM>", DmarcResult.Pass)]
    [InlineData("\"Doe, Jane\" <jane@example.com>", DmarcResult.Pass)]
    [InlineData("\"evil@example.org\" <sender@example.com>", DmarcResult.Pass)]
    [InlineData("sender@example.org", DmarcResult.Unknown)]
    [InlineData("\"sender@example.com\" <sender@example.org>", DmarcResult.Unknown)]
    [InlineData("Sender <sender@example.com>, Other <other@example.org>", DmarcResult.Unknown)]
    [InlineData("Other <other@example.org>, Sender <sender@example.com>", DmarcResult.Unknown)]
    [InlineData("Group: sender@example.com;", DmarcResult.Unknown)]
    [InlineData("Sender <sender@example.com", DmarcResult.Unknown)]
    [InlineData("\"Sender <sender@example.com>", DmarcResult.Unknown)]
    [InlineData("Sender <<sender@example.com>>", DmarcResult.Unknown)]
    [InlineData("Sender <sender@[192.0.2.1]>", DmarcResult.Unknown)]
    [InlineData("Sender <sender@>", DmarcResult.Unknown)]
    [InlineData("Sender <sender>", DmarcResult.Unknown)]
    [InlineData("", DmarcResult.Unknown)]
    [InlineData(null, DmarcResult.Unknown)]
    public void Only_a_single_readable_from_address_can_be_verified(string? from, DmarcResult expected)
    {
        Assert.Equal(expected, Evaluate(["mx.example.net; dmarc=pass header.from=example.com"], from));
    }

    [Theory]
    [InlineData("sender@example.com", "example.com")]
    [InlineData("Sender <Sender@Example.COM.>", "example.com")]
    [InlineData("\"a, b\" <x@example.org>", "example.org")]
    [InlineData("\"quoted \\\" <inner@evil.example.org>\" <real@example.net>", "example.net")]
    [InlineData("x@bücher.example", "xn--bcher-kva.example")]
    [InlineData("sender@example.com, other@example.org", null)]
    [InlineData("sender@exa mple.com", null)]
    [InlineData("sender@example.com;", null)]
    [InlineData("sender@", null)]
    [InlineData("@example.com", null)]
    [InlineData("sender@.example.com", null)]
    [InlineData("sender@example.com/path", null)]
    public void The_from_domain_is_the_one_address_domain_in_ascii_form(string from, string? expected)
    {
        Assert.Equal(expected, SenderAuthentication.FromDomain(from));
    }

    [Fact]
    public void An_internationalized_from_domain_matches_a_header_from_written_either_way()
    {
        Assert.Equal(
            DmarcResult.Pass,
            Evaluate(["mx.example.net; dmarc=pass header.from=xn--bcher-kva.example"], "x@bücher.example"));

        Assert.Equal(
            DmarcResult.Pass,
            Evaluate(["mx.example.net; dmarc=pass header.from=bücher.example"], "x@xn--bcher-kva.example"));
    }

    [Fact]
    public void Dmarc_is_not_evaluated_without_headers_or_trust_and_unknown_when_nothing_usable_was_seen()
    {
        Assert.Null(SenderAuthentication.Dmarc(null, From, Trust));
        Assert.Null(SenderAuthentication.Dmarc(SignalHeaders.None, From, AuthservTrust.None));
        Assert.Equal(DmarcResult.Unknown, SenderAuthentication.Dmarc(SignalHeaders.None, From, Trust));
        Assert.Equal(DmarcResult.Unknown, Evaluate([]));
    }

    [Fact]
    public void Hostile_input_never_throws_and_never_yields_pass_without_a_trusted_instance()
    {
        var random = new Random(8601);
        const string alphabet = "ab;=/()\"\\ \t\r\n.@-_dmarc=pass header.from example.com";

        for (var i = 0; i < 3_000; i++)
        {
            var chars = new char[random.Next(0, 160)];
            for (var j = 0; j < chars.Length; j++) chars[j] = alphabet[random.Next(alphabet.Length)];

            var instance = "other.example.org" + new string(chars);
            Assert.NotEqual(DmarcResult.Pass, Evaluate([instance]));
        }
    }

    [Fact]
    public void An_uninitialized_result_is_unknown_never_a_pass()
    {
        Assert.Equal(DmarcResult.Unknown, default(DmarcResult));
    }

    [Fact]
    public void Stored_names_round_trip()
    {
        foreach (var result in Enum.GetValues<DmarcResult>())
            Assert.Equal(result, DmarcResults.FromStored(result.ToStored()));

        Assert.Equal("unknown", DmarcResult.Imported.ToWire());
        Assert.Null(DmarcResults.FromStored("PASS"));
        Assert.Null(DmarcResults.FromStored(null));
        Assert.Null(DmarcResults.FromStored("verified"));
    }
}
