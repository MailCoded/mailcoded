using Mailcoded.Core.Domain.Identities;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class SignatureTextTests
{
    private static string Normalize(string raw)
    {
        Assert.True(SignatureText.TryNormalize(raw, out var text, out var error), error);
        return text;
    }

    [Theory]
    [InlineData("Alex Example", "Alex Example")]
    [InlineData("Alex Example\n", "Alex Example")]
    [InlineData("\n\n  Alex Example  \n\n", "  Alex Example")]
    [InlineData("Alex\r\nExample Pty\r\n", "Alex\nExample Pty")]
    [InlineData("Alex\rExample", "Alex\nExample")]
    [InlineData("Alex   \t\nExample", "Alex\nExample")]
    [InlineData("Alex\n\nExample", "Alex\n\nExample")]
    [InlineData("-- \nAlex", "Alex")]
    [InlineData("--\nAlex", "Alex")]
    [InlineData("\n-- \n\nAlex", "Alex")]
    [InlineData("Alex\n-- \nExample", "Alex\n--\nExample")]
    [InlineData("Name\tTitle", "Name\tTitle")]
    public void Line_endings_and_edges_are_normalized(string raw, string expected)
    {
        var text = Normalize(raw);

        Assert.DoesNotContain('\r', text);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Control_characters_are_stripped_not_rejected()
    {
        Assert.Equal("AlexExample", Normalize("Alex\u0000\u0001\u0007\u007FExample"));
        Assert.Equal("Alex[31mred", Normalize("Alex\u001B[31mred"));
        Assert.Equal("Alex", Normalize("Al\u0008ex"));
        Assert.Equal("AlexEx", Normalize("Alex\u0090\u009FEx"));
    }

    [Theory]
    [InlineData("Alex‮evil")]
    [InlineData("Alex‪‫‬‭")]
    [InlineData("Alex⁦⁧⁨⁩")]
    [InlineData("Alex​‌‍‎‏")]
    [InlineData("Alex﻿⁠­؜")]
    public void Bidirectional_overrides_and_zero_width_characters_are_stripped(string raw)
    {
        Assert.Equal("Alex" + (raw.Contains("evil", StringComparison.Ordinal) ? "evil" : string.Empty), Normalize(raw));
    }

    [Fact]
    public void Unicode_line_separators_become_line_breaks_and_cannot_forge_a_header()
    {
        var text = Normalize("Alex\u2028Bcc: victim@example.org\u2029Next\u0085End");

        Assert.Equal("Alex\nBcc: victim@example.org\nNext\nEnd", text);
        Assert.DoesNotContain('\u2028', text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void Ordinary_unicode_text_is_kept()
    {
        Assert.Equal("山田 太郎\nExample 株式会社 \U0001F600", Normalize("山田 太郎\nExample 株式会社 \U0001F600"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    [InlineData("--")]
    [InlineData("-- \n\n")]
    [InlineData("​\u0000‮")]
    public void Nothing_left_after_cleaning_is_refused(string? raw)
    {
        Assert.False(SignatureText.TryNormalize(raw, out var text, out var error));
        Assert.Equal(string.Empty, text);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_character_cap_is_a_refusal_not_a_cut()
    {
        Assert.True(SignatureText.TryNormalize(new string('x', SignatureText.MaxChars), out var exact, out _));
        Assert.Equal(SignatureText.MaxChars, exact.Length);

        Assert.False(SignatureText.TryNormalize(new string('x', SignatureText.MaxChars + 1), out var text, out var error));
        Assert.Equal(string.Empty, text);
        Assert.Contains(SignatureText.MaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture), error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_cap_is_a_refusal_not_a_cut()
    {
        Assert.True(SignatureText.TryNormalize(string.Join('\n', Enumerable.Repeat("x", SignatureText.MaxLines)), out _, out _));
        Assert.False(SignatureText.TryNormalize(string.Join('\n', Enumerable.Repeat("x", SignatureText.MaxLines + 1)), out _, out _));
    }

    [Fact]
    public void An_enormous_input_is_refused_before_it_is_processed()
    {
        Assert.False(SignatureText.TryNormalize(new string('\n', 10_000_000), out _, out var error));
        Assert.NotEmpty(error);

        Assert.False(SignatureText.TryNormalize(new string('\u0000', 1_000_000), out _, out _));
    }

    [Theory]
    [InlineData("work", "work")]
    [InlineData("  Work  ", "work")]
    [InlineData("Team.A_b-1", "team.a_b-1")]
    [InlineData("x", "x")]
    public void A_name_is_lower_cased_and_trimmed(string raw, string expected)
    {
        Assert.True(SignatureText.TryNormalizeName(raw, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a\nb")]
    [InlineData("naïve")]
    [InlineData("‮work")]
    [InlineData("work​")]
    [InlineData("0123456789012345678901234567890123")]
    public void A_name_outside_the_charset_or_too_long_is_refused(string? raw)
    {
        Assert.False(SignatureText.TryNormalizeName(raw, out var name));
        Assert.Equal(string.Empty, name);
    }
}
