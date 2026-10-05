using Mailcoded.Core.Domain.Signals;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class AuthenticationResultsParserTests
{
    private static ParsedAuthenticationResults Parsed(string header) =>
        AuthenticationResultsParser.Parse(header) ?? throw new Xunit.Sdk.XunitException($"Expected '{header}' to parse.");

    [Fact]
    public void A_typical_header_yields_the_authserv_id_and_each_method_result()
    {
        var parsed = Parsed(
            "mx.example.net;\r\n dkim=pass header.i=@example.com header.s=sel1 header.b=Ab/Cd+Ef==;\r\n"
            + " spf=pass smtp.mailfrom=sender@example.com;\r\n dmarc=pass (p=NONE sp=NONE dis=NONE) header.from=example.com");

        Assert.Equal("mx.example.net", parsed.AuthservId);
        Assert.Equal(["dkim", "spf", "dmarc"], parsed.Results.Select(r => r.Method));
        Assert.Equal(["pass", "pass", "pass"], parsed.Results.Select(r => r.Result));

        var dkim = parsed.Results[0];
        Assert.Equal(new AuthenticationProperty("header", "b", "Ab/Cd+Ef=="), dkim.Properties[2]);
        Assert.Equal(new AuthenticationProperty("header", "from", "example.com"), parsed.Results[2].Properties.Single());
    }

    [Fact]
    public void The_authserv_id_may_carry_a_version_and_the_no_result_form_has_no_results()
    {
        var versioned = Parsed("example.org 1; spf=pass smtp.mailfrom=a@example.com");
        Assert.Equal("example.org", versioned.AuthservId);
        Assert.Single(versioned.Results);

        var none = Parsed("example.org 1; none");
        Assert.Equal("example.org", none.AuthservId);
        Assert.Empty(none.Results);
    }

    [Fact]
    public void Method_versions_reasons_and_spacing_around_equals_are_read()
    {
        var parsed = Parsed("mx.example.net; dkim/1 = pass reason=\"good signature\" header.d = example.com");

        var result = Assert.Single(parsed.Results);
        Assert.Equal("dkim", result.Method);
        Assert.Equal("1", result.MethodVersion);
        Assert.Equal("pass", result.Result);
        Assert.Equal(new AuthenticationProperty("header", "d", "example.com"), Assert.Single(result.Properties));
    }

    [Theory]
    [InlineData("MX.Example.NET; DMARC=PASS HEADER.FROM=EXAMPLE.COM", "MX.Example.NET", "DMARC", "PASS")]
    [InlineData("mx.example.net;\tdmarc\t=\tpass", "mx.example.net", "dmarc", "pass")]
    [InlineData("mx.example.net;\r\n\tdmarc=pass\r\n\theader.from=example.com", "mx.example.net", "dmarc", "pass")]
    [InlineData("  mx.example.net  ;  dmarc=pass  ", "mx.example.net", "dmarc", "pass")]
    [InlineData("\"mx.example.net\"; dmarc=pass", "mx.example.net", "dmarc", "pass")]
    public void Case_and_whitespace_are_tolerated(string header, string id, string method, string result)
    {
        var parsed = Parsed(header);

        Assert.Equal(id, parsed.AuthservId);
        Assert.Equal(method, parsed.Results[0].Method);
        Assert.Equal(result, parsed.Results[0].Result);
    }

    [Theory]
    [InlineData("mx.example.net; spf=none (dmarc=pass; header.from=example.com)")]
    [InlineData("mx.example.net; spf=none (outer (dmarc=pass) still outer ; dmarc=pass) smtp.helo=a.example.com")]
    [InlineData("mx.example.net; spf=none (a \\) ; dmarc=pass ; b)")]
    [InlineData("mx.example.net; spf=none (\" ) dmarc=pass header.from=example.com")]
    [InlineData("mx.example.net; spf=none smtp.mailfrom=\"a; dmarc=pass header.from=example.com\"")]
    [InlineData("mx.example.net; spf=none smtp.mailfrom=\"a\\\"; dmarc=pass; \"")]
    public void Text_inside_a_comment_or_a_quoted_string_never_yields_a_result(string header)
    {
        var parsed = Parsed(header);

        var result = Assert.Single(parsed.Results);
        Assert.Equal("spf", result.Method);
    }

    [Fact]
    public void A_comment_between_tokens_acts_as_whitespace_not_as_glue()
    {
        var parsed = Parsed("mx.example.net; dmarc(c1)=(c2)pass(c3) header.from=(c4)example.com");

        var result = Assert.Single(parsed.Results);
        Assert.Equal("pass", result.Result);
        Assert.Equal("example.com", Assert.Single(result.Properties).Value);

        var glued = Parsed("mx.example.net; dmarc=pa(x)ss");
        Assert.Equal("pa", glued.Results.Single().Result);
    }

    [Fact]
    public void A_pass_smuggled_through_an_unquoted_value_is_not_the_exact_word_pass()
    {
        var parsed = Parsed("mx.example.net; spf=fail smtp.mailfrom=a;dmarc=pass@example.com");

        Assert.Equal(["spf", "dmarc"], parsed.Results.Select(r => r.Method));
        Assert.Equal("pass@example.com", parsed.Results[1].Result);
    }

    [Fact]
    public void A_key_value_pair_inside_a_result_never_becomes_a_new_result()
    {
        var parsed = Parsed("mx.example.net; dmarc=fail action=none dmarc=pass header.from=example.com");

        var result = Assert.Single(parsed.Results);
        Assert.Equal("fail", result.Result);
    }

    [Theory]
    [InlineData("\"dmarc\"=pass")]
    [InlineData("dmarc=\"pass\"")]
    [InlineData("dmarc")]
    [InlineData("dmarc=")]
    [InlineData("=pass")]
    [InlineData("/1=pass")]
    [InlineData("dmarc pass")]
    [InlineData("pass")]
    [InlineData("dkim/=pass")]
    public void Segments_that_are_not_a_method_result_pair_are_skipped(string segment)
    {
        var parsed = Parsed("mx.example.net; " + segment);

        Assert.Empty(parsed.Results);
    }

    [Theory]
    [InlineData("mx.example.net; dmarc=pass (never closed")]
    [InlineData("mx.example.net; dmarc=pass \"never closed")]
    [InlineData("mx.example.net; dmarc=pass)")]
    [InlineData("mx.example.net; spf=none (nested (never closed) dmarc=pass")]
    public void A_value_that_cannot_be_read_whole_is_refused_rather_than_guessed_at(string header)
    {
        Assert.Null(AuthenticationResultsParser.Parse(header));
        Assert.Equal("mx.example.net", AuthenticationResultsParser.ReadAuthservId(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData("; dmarc=pass")]
    [InlineData("(mx.example.net); dmarc=pass")]
    [InlineData("= ; dmarc=pass")]
    [InlineData("\"\"; dmarc=pass")]
    public void A_missing_authserv_id_means_nothing_is_read(string? header)
    {
        Assert.Null(AuthenticationResultsParser.Parse(header));
        Assert.Null(AuthenticationResultsParser.ReadAuthservId(header));
    }

    [Fact]
    public void An_unreadable_authserv_id_is_reported_as_null_even_when_later_text_is_fine()
    {
        Assert.Null(AuthenticationResultsParser.ReadAuthservId("\"unterminated; dmarc=pass"));
        Assert.Null(AuthenticationResultsParser.ReadAuthservId("(unterminated mx.example.net; dmarc=pass"));
    }

    [Fact]
    public void Input_over_the_length_cap_is_refused_before_any_work_is_done()
    {
        var huge = "mx.example.net; dmarc=pass header.from=" + new string('a', AuthenticationResultsParser.MaxLength);

        Assert.Null(AuthenticationResultsParser.Parse(huge));
        Assert.Null(AuthenticationResultsParser.ReadAuthservId(huge));
    }

    [Fact]
    public void Deep_nesting_and_long_runs_are_handled_in_linear_time_without_throwing()
    {
        var depth = AuthenticationResultsParser.MaxLength / 2 - 60;
        var nested = "mx.example.net; spf=none " + new string('(', depth) + new string(')', depth) + " smtp.helo=a.example.com";
        Assert.Single(Parsed(nested).Results);

        var unbalanced = "mx.example.net; spf=none " + new string('(', depth);
        Assert.Null(AuthenticationResultsParser.Parse(unbalanced));

        var semicolons = "mx.example.net" + string.Concat(Enumerable.Repeat("; x=y", 1_000));
        Assert.Equal(1_000, Parsed(semicolons).Results.Count);

        var quotes = "mx.example.net; spf=none " + string.Concat(Enumerable.Repeat("\"a\" ", 1_500));
        Assert.Single(Parsed(quotes).Results);
    }

    [Fact]
    public void Random_hostile_input_never_throws()
    {
        var random = new Random(8601);
        const string alphabet = "ab;=/()\"\\ \t\r\n.@-_dmarc=pass header.from";

        for (var i = 0; i < 4_000; i++)
        {
            var length = random.Next(0, 120);
            var chars = new char[length];
            for (var j = 0; j < length; j++) chars[j] = alphabet[random.Next(alphabet.Length)];

            var text = new string(chars);
            _ = AuthenticationResultsParser.Parse(text);
            _ = AuthenticationResultsParser.ReadAuthservId(text);
        }
    }
}
