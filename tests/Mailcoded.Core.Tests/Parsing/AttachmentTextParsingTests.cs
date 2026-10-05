using System.Diagnostics;
using System.Text;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class AttachmentTextParsingTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    private static ParsedMessage Parse(byte[] raw, MessageParserOptions? options = null) =>
        (options is null ? MessageParser.Default : new MessageParser(options)).Parse(raw, InternalDate, TestContext.Current.CancellationToken);

    [Fact]
    public void A_csv_attachment_is_indexed_under_its_attachment_index()
    {
        var raw = DocxKit.Message("Numbers", DocxKit.Text("text/csv", "q3.csv", "region,total\nnorth,1200\nsouth,950\n"));

        var parsed = Parse(raw);

        var text = Assert.Single(parsed.AttachmentTexts);
        Assert.Equal(parsed.Attachments[0].Index, text.Index);
        Assert.Equal("text/csv", text.MimeType);
        Assert.Equal("q3.csv", text.FileName);
        Assert.Equal("region,total\nnorth,1200\nsouth,950", text.Text);
        Assert.False(text.Truncated);
    }

    [Fact]
    public void A_docx_attachment_yields_its_text()
    {
        var raw = DocxKit.Message("Minutes", DocxKit.DocxPart(DocxKit.Paragraphs("Action items", "Order the whiteboard")));

        var text = Assert.Single(Parse(raw).AttachmentTexts);

        Assert.Equal("Action items\nOrder the whiteboard", text.Text);
        Assert.Equal("notes.docx", text.FileName);
    }

    [Fact]
    public void A_docx_sent_as_octet_stream_is_recognised_by_its_name()
    {
        var raw = DocxKit.Message("Minutes", DocxKit.Attach("application/octet-stream", "plan.DOCX", DocxKit.Paragraphs("Named by extension")));

        Assert.Equal("Named by extension", Assert.Single(Parse(raw).AttachmentTexts).Text);
    }

    [Fact]
    public void Only_attachments_with_readable_types_are_read()
    {
        var raw = DocxKit.Message(
            "Mixed",
            DocxKit.Attach("application/pdf", "scan.pdf", "%PDF-1.7 secret words"u8.ToArray()),
            DocxKit.Attach("image/png", "logo.png", [0x89, 0x50, 0x4E, 0x47]),
            DocxKit.Attach("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "sheet.xlsx", DocxKit.Paragraphs("not word")),
            DocxKit.Text("text/plain", "notes.txt", "plain notes"));

        var texts = Parse(raw).AttachmentTexts;

        var text = Assert.Single(texts);
        Assert.Equal("plain notes", text.Text);
    }

    [Fact]
    public void An_html_attachment_is_reduced_to_text()
    {
        var raw = DocxKit.Message("Receipt", DocxKit.Text("text/html", "receipt.html", "<html><body><p>Total <b>42</b> dollars</p></body></html>"));

        var text = Assert.Single(Parse(raw).AttachmentTexts);

        Assert.Contains("Total", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_file_labelled_text_is_not_indexed()
    {
        var binary = new byte[4000];
        new Random(5).NextBytes(binary);
        binary[10] = 0;
        var raw = DocxKit.Message("Mislabelled", DocxKit.Attach("text/plain", "payload.txt", binary));

        var parsed = Parse(raw);

        Assert.Empty(parsed.AttachmentTexts);
        Assert.Contains("attachment-text-skipped:binary", parsed.ParseWarnings);
    }

    [Fact]
    public void Text_without_nul_but_mostly_control_bytes_is_not_indexed()
    {
        var bytes = new byte[2000];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(1 + (i % 7));
        var raw = DocxKit.Message("Controls", DocxKit.Attach("text/plain", "ctl.txt", bytes));

        Assert.Empty(Parse(raw).AttachmentTexts);
    }

    [Fact]
    public void A_declared_charset_is_honoured()
    {
        var latin1 = Encoding.Latin1.GetBytes("café déjà vu");
        var raw = DocxKit.Message("Latin", DocxKit.Attach("text/plain; charset=iso-8859-1", "a.txt", latin1));

        Assert.Equal("café déjà vu", Assert.Single(Parse(raw).AttachmentTexts).Text);
    }

    [Fact]
    public void An_undeclared_charset_reads_utf8_and_falls_back_for_legacy_bytes()
    {
        var utf8 = DocxKit.Message("Utf8", DocxKit.Attach("text/plain", "a.txt", Encoding.UTF8.GetBytes("项目进度 naïve")));
        var legacy = DocxKit.Message("Legacy", DocxKit.Attach("text/plain", "b.txt", Encoding.Latin1.GetBytes("naïve café")));

        Assert.Equal("项目进度 naïve", Assert.Single(Parse(utf8).AttachmentTexts).Text);
        Assert.Contains("na", Assert.Single(Parse(legacy).AttachmentTexts).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_utf16_byte_order_mark_is_honoured()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide notes")).ToArray();
        var raw = DocxKit.Message("Wide", DocxKit.Attach("text/plain", "w.txt", bytes));

        Assert.Equal("wide notes", Assert.Single(Parse(raw).AttachmentTexts).Text);
    }

    [Fact]
    public void An_attachment_larger_than_the_byte_budget_is_cut_and_marked()
    {
        var big = string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 80_000));
        var raw = DocxKit.Message("Big", DocxKit.Text("text/plain", "big.txt", big));

        var parsed = Parse(raw, new MessageParserOptions { AttachmentText = new AttachmentTextLimits { MaxTextBytes = 100_000 } });

        var text = Assert.Single(parsed.AttachmentTexts);
        Assert.True(text.Truncated);
        Assert.InRange(text.Text.Length, 90_000, 100_000);
    }

    [Fact]
    public void The_per_attachment_and_per_message_character_budgets_hold()
    {
        var chunk = string.Concat(Enumerable.Repeat("word ", 1_000));
        var raw = DocxKit.Message(
            "Budgets",
            DocxKit.Text("text/plain", "a.txt", chunk),
            DocxKit.Text("text/plain", "b.txt", chunk),
            DocxKit.Text("text/plain", "c.txt", chunk));

        var parsed = Parse(raw, new MessageParserOptions
        {
            AttachmentText = new AttachmentTextLimits { MaxCharsPerAttachment = 2_000, MaxCharsPerMessage = 3_500 },
        });

        Assert.Equal(2, parsed.AttachmentTexts.Count);
        Assert.True(parsed.AttachmentTexts.Sum(t => t.Text.Length) <= 3_500);
        Assert.All(parsed.AttachmentTexts, t => Assert.True(t.Truncated));
        Assert.Contains("attachment-text-skipped:text-limit", parsed.ParseWarnings);
    }

    [Fact]
    public void No_more_than_the_attachment_limit_are_read()
    {
        var parts = Enumerable.Range(0, 20).Select(i => DocxKit.Text("text/plain", $"f{i}.txt", $"file number {i}")).ToArray();

        var parsed = Parse(DocxKit.Message("Many", parts));

        Assert.Equal(16, parsed.AttachmentTexts.Count);
        Assert.Contains("attachment-text-skipped:limit", parsed.ParseWarnings);
        Assert.Equal(20, parsed.Attachments.Count);
    }

    [Fact]
    public void Extraction_can_be_switched_off()
    {
        var raw = DocxKit.Message("Off", DocxKit.Text("text/plain", "a.txt", "ignored"));

        Assert.Empty(Parse(raw, new MessageParserOptions { ExtractAttachmentText = false }).AttachmentTexts);
        Assert.Empty(MessageParser.Default.WithoutAttachmentText().Parse(raw, InternalDate, TestContext.Current.CancellationToken).AttachmentTexts);
    }

    [Fact]
    public void The_lean_parser_is_cached_and_idempotent()
    {
        var lean = MessageParser.Default.WithoutAttachmentText();

        Assert.Same(lean, MessageParser.Default.WithoutAttachmentText());
        Assert.Same(lean, lean.WithoutAttachmentText());
    }

    [Fact]
    public void A_hostile_docx_inside_a_message_costs_a_warning_and_nothing_else()
    {
        var inflated = new byte[12 * 1024 * 1024];
        Array.Fill(inflated, (byte)'a');
        var bomb = DocxKit.Zip(("word/document.xml", inflated));
        var oversized = new byte[48 * 1024 * 1024];
        Array.Fill(oversized, (byte)'a');
        var huge = DocxKit.Zip(("word/document.xml", oversized));
        var xxe = DocxKit.Docx(
            "<!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><w:document " + DocxKit.Wml + "><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>");
        var raw = DocxKit.Message(
            "Hostile",
            DocxKit.DocxPart(bomb, "bomb.docx"),
            DocxKit.DocxPart(huge, "huge.docx"),
            DocxKit.DocxPart(xxe, "xxe.docx"),
            DocxKit.DocxPart("PK\u0003\u0004 not really a zip"u8.ToArray(), "fake.docx"),
            DocxKit.Text("text/plain", "ok.txt", "survivor"));

        var clock = Stopwatch.StartNew();
        var parsed = Parse(raw);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        var text = Assert.Single(parsed.AttachmentTexts);
        Assert.Equal("survivor", text.Text);
        Assert.Contains("attachment-text-skipped:docx-compressionratio", parsed.ParseWarnings);
        Assert.Contains("attachment-text-skipped:docx-toolarge", parsed.ParseWarnings);
        Assert.Contains("attachment-text-skipped:docx-refused", parsed.ParseWarnings);
        Assert.Contains("attachment-text-skipped:docx-notazip", parsed.ParseWarnings);
        Assert.Equal(5, parsed.Attachments.Count);
    }

    [Fact]
    public void A_docx_larger_than_the_container_limit_is_skipped_without_being_opened()
    {
        var raw = DocxKit.Message("Large", DocxKit.DocxPart(DocxKit.Paragraphs("fine"), "a.docx"));

        var parsed = Parse(raw, new MessageParserOptions { AttachmentText = new AttachmentTextLimits { MaxDocxBytes = 100 } });

        Assert.Empty(parsed.AttachmentTexts);
        Assert.Contains("attachment-text-skipped:docx-too-large", parsed.ParseWarnings);
    }

    [Fact]
    public void An_exhausted_time_budget_skips_the_rest()
    {
        var raw = DocxKit.Message("Slow", DocxKit.Text("text/plain", "a.txt", "one"), DocxKit.Text("text/plain", "b.txt", "two"));

        var parsed = Parse(raw, new MessageParserOptions { AttachmentText = new AttachmentTextLimits { TimeBudget = TimeSpan.Zero } });

        Assert.Empty(parsed.AttachmentTexts);
        Assert.Contains("attachment-text-skipped:time-budget", parsed.ParseWarnings);
    }

    [Fact]
    public void Indexes_agree_with_the_attachment_the_copier_returns()
    {
        var raw = DocxKit.Message(
            "Agree",
            DocxKit.Attach("image/png", "a.png", [1, 2, 3]),
            DocxKit.Text("text/plain", "b.txt", "second attachment text"),
            DocxKit.Attach("application/pdf", "c.pdf", [4, 5, 6]));

        var parsed = Parse(raw);
        var text = Assert.Single(parsed.AttachmentTexts);

        using var source = new MemoryStream(raw);
        using var copy = new MemoryStream();
        var described = MessageParser.Default.CopyAttachmentTo(source, text.Index, copy, TestContext.Current.CancellationToken);

        Assert.Equal("b.txt", described?.FileName);
        Assert.Equal("second attachment text", Encoding.UTF8.GetString(copy.ToArray()));
    }

    [Fact]
    public void Control_and_bidi_characters_in_attachment_text_never_reach_the_index()
    {
        var raw = DocxKit.Message("Spoof", DocxKit.Text("text/plain", "s.txt", "invoice‮txt.exe​ pay\u001B[2J now"));

        var text = Assert.Single(Parse(raw).AttachmentTexts);

        Assert.DoesNotContain('‮', text.Text);
        Assert.DoesNotContain('​', text.Text);
        Assert.DoesNotContain('\u001B', text.Text);
    }

    [Theory]
    [InlineData("041-pgp-mime.eml")]
    [InlineData("042-smime-enveloped.eml")]
    [InlineData("023-smime-opaque.eml")]
    [InlineData("024-tnef.eml")]
    [InlineData("014-attachment-pdf.eml")]
    [InlineData("015-attachment-hostile-filename.eml")]
    public void Encrypted_and_opaque_containers_in_the_corpus_yield_no_attachment_text(string fixture)
    {
        var parsed = MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, TestContext.Current.CancellationToken);

        Assert.Empty(parsed.AttachmentTexts);
    }

    [Fact]
    public void Every_corpus_fixture_parses_with_extraction_inside_a_second()
    {
        var clock = Stopwatch.StartNew();

        foreach (var name in Fixtures.EmlFileNames())
        {
            var parsed = MessageParser.Default.Parse(Fixtures.Read(name), InternalDate, TestContext.Current.CancellationToken);
            Assert.NotNull(parsed.AttachmentTexts);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }
}
