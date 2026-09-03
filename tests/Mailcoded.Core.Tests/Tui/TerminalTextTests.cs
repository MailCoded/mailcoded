using System.Text;
using Mailcoded.Tui.Render;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

/// <summary>CLAUDE invariant 4 names terminals beside eval and shell commands. This is the choke point.</summary>
public sealed class TerminalTextTests
{
    private const char Esc = '\u001b';
    private const char Replacement = '\ufffd';

    public static TheoryData<string, string> HostileStrings() => new()
    {
        { "\u001b[2J\u001b[H", "wipe the screen" },
        { "\u001b]0;pwned\u0007", "set the window title" },
        { "\u001b]8;;http://evil.example\u001b\\click\u001b]8;;\u001b\\", "OSC 8 hyperlink" },
        { "\u001b]52;c;cGF5bG9hZA==\u0007", "OSC 52 clipboard write" },
        { "\u009b2J", "8-bit CSI" },
        { "\u009d52;c;x\u009c", "8-bit OSC" },
        { "before\u0000after", "NUL" },
        { "bell\u0007bell", "BEL" },
        { "back\u0008space", "backspace" },
        { "carriage\rreturn", "CR overwrite" },
        { "del\u007fete", "DEL" },
        { "\u202egnp.exe", "right-to-left override" },
        { "a\u202db", "left-to-right override" },
        { "a\u2066b\u2069c", "isolate" },
        { "zero\u200bwidth", "zero-width space" },
        { "join\u200der", "zero-width joiner" },
        { "\ufeffbom", "byte order mark" },
        { "soft\u00adhyphen", "soft hyphen" },
        { "tag\U000E0041block", "astral tag character" },
        { "line\u2028separator", "line separator" },
        { "para\u2029separator", "paragraph separator" },
        { "private\ue000use", "private use area" },
    };

    [Theory]
    [MemberData(nameof(HostileStrings))]
    public void Cell_lets_no_escape_reach_the_terminal(string hostile, string why)
    {
        var rendered = TerminalText.Cell(hostile, 200).Text;

        Assert.False(
            rendered.Any(IsDangerous),
            $"A sequence that would {why} survived rendering: {Describe(rendered)}");
    }

    [Theory]
    [MemberData(nameof(HostileStrings))]
    public void Wrap_lets_no_escape_reach_the_terminal(string hostile, string why)
    {
        foreach (var line in TerminalText.Wrap(hostile, 40, 10))
        {
            Assert.False(
                line.Text.Any(IsDangerous),
                $"A sequence that would {why} survived wrapping: {Describe(line.Text)}");
        }
    }

    [Fact]
    public void A_disallowed_scalar_becomes_a_visible_replacement_not_a_silent_gap()
    {
        Assert.Equal($"gnp{Replacement}.exe", TerminalText.Cell("gnp\u202E.exe", 40).Text);
    }

    [Fact]
    public void Bidi_overrides_cannot_reorder_a_displayed_filename()
    {
        var spoofed = "invoice\u202Egnp.exe";
        var rendered = TerminalText.Cell(spoofed, 40).Text;

        Assert.DoesNotContain('\u202E', rendered);
        Assert.Contains(Replacement, rendered);
    }

    [Fact]
    public void Whitespace_of_every_kind_collapses_to_one_ordinary_space()
    {
        Assert.Equal("a b", TerminalText.Cell("a\t\r\n   b", 40).Text);
    }

    [Fact]
    public void A_surrogate_pair_is_never_split_at_the_clamp_boundary()
    {
        var text = "ab\U0001F600cd";

        for (var columns = 1; columns <= 8; columns++)
        {
            var rendered = TerminalText.Cell(text, columns).Text;
            Assert.True(IsWellFormed(rendered), $"A lone surrogate survived a clamp to {columns} columns.");
        }
    }

    [Fact]
    public void A_lone_surrogate_is_replaced_rather_than_emitted()
    {
        var rendered = TerminalText.Cell("a\ud800b", 40).Text;

        Assert.DoesNotContain(rendered, char.IsSurrogate);
        Assert.Contains(Replacement, rendered);
    }

    [Fact]
    public void A_combining_mark_cannot_open_a_cell_and_climb_onto_the_chrome()
    {
        var rendered = TerminalText.Cell("\u0301abc", 40).Text;

        Assert.StartsWith(Replacement.ToString(), rendered);
    }

    [Fact]
    public void A_zalgo_cluster_is_replaced_rather_than_allowed_to_bleed_across_rows()
    {
        var zalgo = "a" + new string('\u0301', 40);
        var rendered = TerminalText.Cell(zalgo, 40);

        Assert.Equal(Replacement.ToString(), rendered.Text);
        Assert.Equal(1, rendered.Columns);
    }

    [Theory]
    [InlineData("hello", 5)]
    [InlineData("你好", 4)]
    [InlineData("ＡＢ", 4)]
    [InlineData("é", 1)]
    [InlineData("\U0001F600", 2)]
    [InlineData("한국", 4)]
    public void Column_accounting_matches_what_a_terminal_advances(string text, int expected)
    {
        Assert.Equal(expected, TerminalText.Cell(text, 80).Columns);
    }

    [Fact]
    public void A_wide_glyph_is_dropped_rather_than_half_drawn_at_the_edge()
    {
        var rendered = TerminalText.Cell("a你", 2);

        Assert.Equal("a", rendered.Text);
        Assert.Equal(1, rendered.Columns);
    }

    [Fact]
    public void No_cell_ever_reports_more_columns_than_it_was_given()
    {
        foreach (var hostile in HostileStrings().Select(row => row.Data.Item1))
        {
            for (var columns = 1; columns <= 12; columns++)
            {
                var rendered = TerminalText.Cell(hostile, columns);
                Assert.True(
                    rendered.Columns <= columns,
                    $"'{Describe(hostile)}' reported {rendered.Columns} columns in a {columns}-column cell.");
            }
        }
    }

    [Fact]
    public void A_hundred_thousand_character_line_is_clamped_before_it_reaches_a_row()
    {
        var monster = new string('x', 100_000);
        var rendered = TerminalText.Cell(monster, 80);

        Assert.Equal(80, rendered.Columns);
        Assert.Equal(80, rendered.Text.Length);
    }

    [Fact]
    public void A_hundred_thousand_character_body_wraps_to_the_line_budget_it_was_given()
    {
        var monster = string.Join(' ', Enumerable.Repeat("word", 30_000));
        var lines = TerminalText.Wrap(monster, 78, 24);

        Assert.Equal(24, lines.Count);
        Assert.All(lines, line => Assert.True(line.Columns <= 78));
    }

    [Fact]
    public void Wrap_breaks_on_word_boundaries_when_a_word_fits_on_a_line_of_its_own()
    {
        var lines = TerminalText.Wrap("alpha bravo charlie", 12, 10);

        Assert.Equal(["alpha bravo", "charlie"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Wrap_hard_breaks_a_word_too_long_for_any_line()
    {
        var lines = TerminalText.Wrap(new string('z', 25), 10, 10);

        Assert.Equal(3, lines.Count);
        Assert.Equal(10, lines[0].Columns);
    }

    [Fact]
    public void Wrap_keeps_the_paragraph_structure_of_a_plaintext_body()
    {
        var lines = TerminalText.Wrap("one\r\ntwo\n\nthree", 20, 10);

        Assert.Equal(["one", "two", string.Empty, "three"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Clip_never_lengthens_and_never_splits_a_cluster()
    {
        var span = TerminalText.Cell("你好世界", 80);

        for (var columns = 0; columns <= 10; columns++)
        {
            var clipped = TerminalText.Clip(span, columns);
            Assert.True(clipped.Columns <= Math.Max(columns, 0));
            Assert.Equal(0, clipped.Columns % 2);
        }
    }

    [Fact]
    public void Pad_produces_exactly_the_requested_width()
    {
        Assert.Equal(20, TerminalText.Pad(TerminalText.Cell("你好", 80), 20).Columns);
        Assert.Equal(3, TerminalText.Pad(TerminalText.Cell("abcdef", 80), 3).Columns);
    }

    private static bool IsWellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                return false;
            i++;
        }

        return true;
    }

    private static bool IsDangerous(char c) => c < ' ' || c == '\u007f' || c is >= '\u0080' and <= '\u009f';

    private static string Describe(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value) builder.Append(c < ' ' ? $"\\u{(int)c:x4}" : c.ToString());
        return builder.ToString();
    }
}
