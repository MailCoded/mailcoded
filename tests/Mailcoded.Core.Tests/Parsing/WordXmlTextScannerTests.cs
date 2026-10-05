using System.Diagnostics;
using System.Text;
using Mailcoded.Core.Parsing;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class WordXmlTextScannerTests
{
    private static (WordXmlScan Result, string Text) Run(string xml, int max = 100_000) => Run(Encoding.UTF8.GetBytes(xml), max);

    private static (WordXmlScan Result, string Text) Run(byte[] xml, int max = 100_000)
    {
        var sb = new StringBuilder();
        var result = WordXmlTextScanner.Scan(xml, sb, max, TestContext.Current.CancellationToken);
        return (result, sb.ToString());
    }

    [Fact]
    public void Runs_of_one_paragraph_join_and_paragraphs_break()
    {
        var (result, text) = Run("<w:p><w:r><w:t>Hel</w:t></w:r><w:r><w:t>lo</w:t></w:r></w:p><w:p><w:r><w:t>World</w:t></w:r></w:p>");

        Assert.Equal(WordXmlScan.Complete, result);
        Assert.Equal("Hello\nWorld\n", text);
    }

    [Fact]
    public void Tabs_breaks_and_cell_ends_become_separators_but_tab_stop_definitions_do_not()
    {
        var (_, text) = Run(
            "<w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr>"
            + "<w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t></w:r><w:tc><w:p><w:r><w:t>d</w:t></w:r></w:p></w:tc>");

        Assert.Equal("a\tb\ncd\n\t", text);
    }

    [Fact]
    public void Only_text_runs_are_text()
    {
        var (_, text) = Run(
            "<w:r><w:instrText>HYPERLINK \"secret\"</w:instrText></w:r><w:del><w:r><w:delText>gone</w:delText></w:r></w:del>"
            + "<w:r><w:t>kept</w:t></w:r><w:r><w:rPr><w:b/></w:rPr>stray text outside a run</w:r>");

        Assert.Equal("kept", text);
    }

    [Fact]
    public void An_alternate_content_fallback_does_not_repeat_the_choice()
    {
        var (_, text) = Run(
            "<mc:AlternateContent><mc:Choice><w:r><w:t>box</w:t></w:r></mc:Choice>"
            + "<mc:Fallback><w:r><w:t>box</w:t></w:r></mc:Fallback></mc:AlternateContent><w:r><w:t>after</w:t></w:r>");

        Assert.Equal("boxafter", text);
    }

    [Fact]
    public void An_unclosed_fallback_swallows_the_rest_rather_than_throwing()
    {
        var (result, text) = Run("<w:r><w:t>one</w:t></w:r><mc:Fallback><w:r><w:t>two</w:t></w:r>");

        Assert.Equal(WordXmlScan.Complete, result);
        Assert.Equal("one", text);
    }

    [Theory]
    [InlineData("a &amp; b &lt;c&gt; &quot;d&quot; &apos;e&apos;", "a & b <c> \"d\" 'e'")]
    [InlineData("&#65;&#x42;&#X43;", "ABC")]
    [InlineData("&#x1F600;", "\U0001F600")]
    [InlineData("&unknown;gone", "gone")]
    [InlineData("&#0;&#1;&#xD800;&#x110000;&#9999999;x", "x")]
    [InlineData("&#99999999999;x", "#99999999999;x")]
    [InlineData("a & b", "a  b")]
    [InlineData("&amp", "amp")]
    public void Entities_resolve_to_text_only_through_the_predefined_five_and_numeric_references(string source, string expected)
    {
        var (result, text) = Run("<w:t>" + source + "</w:t>");

        Assert.Equal(WordXmlScan.Complete, result);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Cdata_inside_a_run_is_text_and_cdata_outside_is_not()
    {
        var (_, text) = Run("<w:t><![CDATA[1 < 2 & 3]]></w:t><![CDATA[outside]]><w:r><w:t>x</w:t></w:r>");

        Assert.Equal("1 < 2 & 3x", text);
    }

    [Fact]
    public void Comments_and_processing_instructions_hide_their_content()
    {
        var (_, text) = Run("<?xml version=\"1.0\"?><w:t>a<!-- <w:t>no</w:t> --></w:t><?pi <w:t>no</w:t> ?><w:t>b</w:t>");

        Assert.Equal("ab", text);
    }

    [Fact]
    public void An_attribute_value_may_hold_a_closing_angle_bracket()
    {
        var (_, text) = Run("<w:t w:x=\"1>2\" w:y='a>b'>kept</w:t>");

        Assert.Equal("kept", text);
    }

    [Fact]
    public void A_byte_order_mark_is_skipped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("<w:t>ok</w:t>")).ToArray();

        Assert.Equal("ok", Run(bytes).Text);
    }

    [Fact]
    public void Multibyte_text_survives()
    {
        Assert.Equal("项目进度 café", Run("<w:t>项目进度 café</w:t>").Text);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x3C, 0x00 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x3C })]
    [InlineData(new byte[] { 0x00, 0x3C, 0x00, 0x77 })]
    public void A_utf16_part_is_refused_rather_than_misread(byte[] head)
    {
        var (result, text) = Run(head);

        Assert.Equal(WordXmlScan.Refused, result);
        Assert.Empty(text);
    }

    [Theory]
    [InlineData("<!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><w:t>&x;</w:t>")]
    [InlineData("<w:t>before</w:t><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;\">]><w:t>&lol2;</w:t>")]
    [InlineData("<w:t>a</w:t><!ENTITY x \"y\">")]
    [InlineData("<w:t>a</w:t><!ELEMENT x ANY>")]
    public void Any_declaration_refuses_the_whole_part_and_discards_what_it_read(string xml)
    {
        var sb = new StringBuilder("earlier");
        var result = WordXmlTextScanner.Scan(Encoding.UTF8.GetBytes(xml), sb, 1000, TestContext.Current.CancellationToken);

        Assert.Equal(WordXmlScan.Refused, result);
        Assert.Equal("earlier", sb.ToString());
    }

    [Fact]
    public void An_external_entity_reference_never_resolves_to_file_content()
    {
        var (_, text) = Run("<w:t>&xxe;&file;&#38;&amp;</w:t>");

        Assert.Equal("&&", text);
    }

    [Theory]
    [InlineData("<w:t>kept</w:t><w:p")]
    [InlineData("<w:t>kept</w:t><w:p attr=\"never closed")]
    [InlineData("<w:t>kept</w:t><!-- never closed")]
    [InlineData("<w:t>kept</w:t><![CDATA[ never closed")]
    [InlineData("<w:t>kept</w:t><?pi never closed")]
    [InlineData("<w:t>kept</w:t><")]
    public void Truncated_markup_ends_the_scan_and_keeps_the_text_before_it(string xml)
    {
        var (result, text) = Run(xml);

        Assert.Equal(WordXmlScan.Malformed, result);
        Assert.Equal("kept", text);
    }

    [Fact]
    public void The_character_budget_stops_the_scan_and_says_so()
    {
        var (result, text) = Run("<w:t>" + new string('x', 5_000) + "</w:t><w:t>more</w:t>", max: 100);

        Assert.Equal(WordXmlScan.Truncated, result);
        Assert.Equal(100, text.Length);
    }

    [Fact]
    public void A_zero_budget_reads_nothing()
    {
        var (result, text) = Run("<w:t>abc</w:t>", max: 0);

        Assert.Equal(WordXmlScan.Truncated, result);
        Assert.Empty(text);
    }

    [Fact]
    public void Nesting_is_never_tracked_so_a_million_open_elements_cost_linear_time()
    {
        var sb = new StringBuilder("<w:p>", 6_000_000);
        for (var i = 0; i < 1_000_000; i++) sb.Append("<a>");
        sb.Append("<w:t>deep</w:t>");

        var clock = Stopwatch.StartNew();
        var (result, text) = Run(sb.ToString(), 1000);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal(WordXmlScan.Complete, result);
        Assert.Equal("deep", text);
    }

    [Fact]
    public void A_tag_with_a_name_of_a_million_bytes_is_skipped_without_trouble()
    {
        var (result, text) = Run("<" + new string('n', 1_000_000) + ">x<w:t>ok</w:t>");

        Assert.Equal(WordXmlScan.Complete, result);
        Assert.Equal("ok", text);
    }

    [Fact]
    public void A_million_stray_ampersands_in_a_run_stay_linear()
    {
        var clock = Stopwatch.StartNew();
        var (_, text) = Run("<w:t>" + new string('&', 1_000_000) + "z</w:t>");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal("z", text);
    }

    [Fact]
    public void Random_bytes_never_throw_and_never_exceed_the_budget()
    {
        var random = new Random(20260105);
        var alphabet = "<>/!?-[]CDATA\"'&;# wtpr:=".ToCharArray();

        for (var round = 0; round < 400; round++)
        {
            var length = random.Next(1, 600);
            var xml = new byte[length];
            for (var i = 0; i < length; i++)
                xml[i] = round % 2 == 0 ? (byte)random.Next(256) : (byte)alphabet[random.Next(alphabet.Length)];

            var sb = new StringBuilder();
            var result = WordXmlTextScanner.Scan(xml, sb, 64, TestContext.Current.CancellationToken);

            Assert.True(sb.Length <= 64 + 1, $"round {round} produced {sb.Length} characters");
            Assert.True(Enum.IsDefined(result));
        }
    }

    [Fact]
    public void A_cancelled_token_stops_a_long_scan()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var sb = new StringBuilder();
        var xml = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<a/>", 100_000)));

        Assert.Throws<OperationCanceledException>(() => WordXmlTextScanner.Scan(xml, sb, 100, source.Token));
    }
}
