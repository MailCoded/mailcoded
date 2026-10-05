using Mailcoded.Core.Domain.Signals;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class AuthservTrustTests
{
    [Theory]
    [InlineData("mx.example.net", "mx.example.net")]
    [InlineData("  MX.Example.NET.  ", "mx.example.net")]
    [InlineData("*.example.net", "*.example.net")]
    [InlineData("*.Mail.Example.Net", "*.mail.example.net")]
    [InlineData("mailhost", "mailhost")]
    [InlineData("mx_1.example-net.org", "mx_1.example-net.org")]
    public void Valid_patterns_are_normalized(string raw, string expected)
    {
        Assert.True(AuthservTrust.TryNormalize(raw, out var pattern));
        Assert.Equal(expected, pattern);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("*.")]
    [InlineData("*.com")]
    [InlineData("**.example.net")]
    [InlineData("mx.*.example.net")]
    [InlineData("mx.example.*")]
    [InlineData("mx..example.net")]
    [InlineData(".example.net")]
    [InlineData("mx example.net")]
    [InlineData("mx.example.net; dmarc=pass")]
    [InlineData("mx.example.net(comment)")]
    [InlineData("\"mx.example.net\"")]
    [InlineData("mx.exämple.net")]
    [InlineData("mx@example.net")]
    public void Anything_that_is_not_a_hostname_or_a_star_suffix_is_refused(string? raw)
    {
        Assert.False(AuthservTrust.TryNormalize(raw, out _));
    }

    [Fact]
    public void A_pattern_over_the_length_cap_is_refused()
    {
        Assert.False(AuthservTrust.TryNormalize(new string('a', AuthservTrust.MaxPatternLength + 1), out _));
        Assert.True(AuthservTrust.TryNormalize(new string('a', AuthservTrust.MaxPatternLength), out _));
    }

    [Fact]
    public void Exact_patterns_match_the_whole_name_only()
    {
        var trust = AuthservTrust.From(["mx.example.net"]);

        Assert.True(trust.Matches("mx.example.net"));
        Assert.True(trust.Matches("MX.EXAMPLE.NET"));
        Assert.True(trust.Matches("mx.example.net."));
        Assert.False(trust.Matches("evil.mx.example.net"));
        Assert.False(trust.Matches("mx.example.net.evil.example.org"));
        Assert.False(trust.Matches("xmx.example.net"));
        Assert.False(trust.Matches("example.net"));
        Assert.False(trust.Matches("mx.example.net x"));
        Assert.False(trust.Matches(""));
        Assert.False(trust.Matches(null));
    }

    [Fact]
    public void A_star_suffix_matches_strictly_deeper_names()
    {
        var trust = AuthservTrust.From(["*.example.net"]);

        Assert.True(trust.Matches("mx.example.net"));
        Assert.True(trust.Matches("a.b.example.net"));
        Assert.False(trust.Matches("example.net"));
        Assert.False(trust.Matches("badexample.net"));
        Assert.False(trust.Matches("mx.example.net.evil.example.org"));
        Assert.False(trust.Matches("*.example.net"));
    }

    [Fact]
    public void An_empty_set_trusts_nothing_and_invalid_or_duplicate_entries_are_dropped()
    {
        Assert.False(AuthservTrust.None.Matches("mx.example.net"));
        Assert.True(AuthservTrust.From(null).IsEmpty);
        Assert.True(AuthservTrust.From(["", "*", "bad name"]).IsEmpty);

        var trust = AuthservTrust.From(["MX.example.net", "mx.example.net.", "bad name", "*.example.org"]);
        Assert.Equal(["mx.example.net", "*.example.org"], trust.Patterns);
    }

    [Fact]
    public void The_set_is_capped_and_a_union_keeps_both_sides()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"mx{i}.example.net");
        Assert.Equal(AuthservTrust.MaxPatterns, AuthservTrust.From(many).Patterns.Count);

        var union = AuthservTrust.From(["a.example.net"]).Union(AuthservTrust.From(["b.example.net", "a.example.net"]));
        Assert.Equal(["a.example.net", "b.example.net"], union.Patterns);
    }
}
