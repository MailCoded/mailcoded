using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class DocxTextExtractorTests
{
    private static readonly long Forever = long.MaxValue;

    private static DocxText Extract(byte[] docx, AttachmentTextLimits? limits = null, int max = 100_000, long? deadline = null) =>
        DocxTextExtractor.Extract(docx, docx.Length, limits ?? AttachmentTextLimits.Default, max, deadline ?? Forever, TestContext.Current.CancellationToken);

    private static void PatchSizes(byte[] zip, uint compressed, uint uncompressed)
    {
        for (var i = 0; i + 46 <= zip.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i));
            if (signature == 0x02014B50u)
            {
                if (compressed != 0) BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 20), compressed);
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 24), uncompressed);
            }
            else if (signature == 0x04034B50u)
            {
                if (compressed != 0) BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 18), compressed);
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 22), uncompressed);
            }
        }
    }

    private static void PatchEntryCount(byte[] zip, ushort total)
    {
        for (var i = zip.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) != 0x06054B50u) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(i + 8), total);
            BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(i + 10), total);
            return;
        }

        throw new InvalidOperationException("no end-of-central-directory record");
    }

    [Fact]
    public void A_plain_document_yields_its_paragraphs()
    {
        var result = Extract(DocxKit.Paragraphs("Quarterly plan", "Budget 2027 &amp; hiring"));

        Assert.True(result.Ok);
        Assert.Equal("Quarterly plan\nBudget 2027 & hiring\n", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Footnotes_and_endnotes_are_read_and_headers_and_footers_are_not()
    {
        var docx = DocxKit.Docx(
            DocxKit.Document("Body"),
            ("word/footnotes.xml", DocxKit.Wrap("<w:footnote><w:p><w:r><w:t>Note one</w:t></w:r></w:p></w:footnote>")),
            ("word/endnotes.xml", DocxKit.Wrap("<w:endnote><w:p><w:r><w:t>End note</w:t></w:r></w:p></w:endnote>")),
            ("word/header1.xml", DocxKit.Wrap("<w:p><w:r><w:t>Confidential header</w:t></w:r></w:p>")),
            ("word/footer1.xml", DocxKit.Wrap("<w:p><w:r><w:t>Page footer</w:t></w:r></w:p>")));

        var result = Extract(docx);

        Assert.True(result.Ok);
        Assert.Contains("Body", result.Text, StringComparison.Ordinal);
        Assert.Contains("Note one", result.Text, StringComparison.Ordinal);
        Assert.Contains("End note", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("header", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("footer", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_container_without_a_main_document_is_no_document()
    {
        var zip = DocxKit.Zip(("word/styles.xml", Encoding.UTF8.GetBytes("<x/>")), ("readme.txt", "hi"u8.ToArray()));

        Assert.Equal(DocxOutcome.NoDocument, Extract(zip).Outcome);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 })]
    public void Bytes_that_are_not_a_zip_are_refused(byte[] data)
    {
        Assert.Equal(DocxOutcome.NotAZip, Extract(data).Outcome);
    }

    [Fact]
    public void A_truncated_zip_never_throws()
    {
        var zip = DocxKit.Paragraphs("kept");

        for (var cut = 1; cut < zip.Length; cut += 7)
        {
            var result = Extract(zip.AsSpan(0, cut).ToArray());
            Assert.False(result.Ok, $"a zip cut to {cut} of {zip.Length} bytes was accepted");
        }
    }

    [Fact]
    public void A_central_directory_that_declares_too_many_entries_is_refused_before_it_is_read()
    {
        var zip = DocxKit.Paragraphs("kept");
        PatchEntryCount(zip, 60_000);

        var result = Extract(zip);

        Assert.Equal(DocxOutcome.TooManyEntries, result.Outcome);
    }

    [Fact]
    public void A_zip64_marker_counts_as_too_many_entries()
    {
        var zip = DocxKit.Paragraphs("kept");
        PatchEntryCount(zip, ushort.MaxValue);

        Assert.Equal(DocxOutcome.TooManyEntries, Extract(zip).Outcome);
    }

    [Fact]
    public void A_real_container_of_thousands_of_entries_is_refused()
    {
        var entries = new List<(string, byte[])> { ("word/document.xml", Encoding.UTF8.GetBytes(DocxKit.Document("x"))) };
        for (var i = 0; i < 5_000; i++) entries.Add(($"media/{i}.bin", []));

        var result = Extract(DocxKit.Zip([.. entries]));

        Assert.Equal(DocxOutcome.TooManyEntries, result.Outcome);
    }

    [Fact]
    public void The_entry_limit_is_the_callers_to_set()
    {
        var zip = DocxKit.Paragraphs("kept");

        Assert.Equal(DocxOutcome.TooManyEntries, Extract(zip, new AttachmentTextLimits { MaxZipEntries = 1 }).Outcome);
        Assert.True(Extract(zip, new AttachmentTextLimits { MaxZipEntries = 2 }).Ok);
    }

    [Fact]
    public void A_zip_bomb_is_refused_on_its_ratio_without_being_inflated()
    {
        var inflated = new byte[64 * 1024 * 1024];
        Array.Fill(inflated, (byte)'a');
        var zip = DocxKit.Zip(("word/document.xml", inflated));
        Assert.True(zip.Length < 200_000, "the test bomb did not compress");

        var clock = Stopwatch.StartNew();
        var result = Extract(zip, new AttachmentTextLimits { MaxXmlPartBytes = 128 * 1024 * 1024, MaxInflatedBytes = 128 * 1024 * 1024 });

        Assert.Equal(DocxOutcome.CompressionRatio, result.Outcome);
        Assert.Empty(result.Text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }

    [Fact]
    public void An_entry_past_the_byte_budget_is_refused_whatever_its_ratio()
    {
        var text = new StringBuilder("<w:t>");
        var random = new Random(7);
        while (text.Length < 3 * 1024 * 1024) text.Append(random.Next(1_000_000, 9_999_999)).Append(' ');
        text.Append("</w:t>");
        var zip = DocxKit.Docx(DocxKit.Wrap(text.ToString()));

        var result = Extract(zip, new AttachmentTextLimits { MaxXmlPartBytes = 1024 * 1024 });

        Assert.Equal(DocxOutcome.TooLarge, result.Outcome);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void A_central_directory_that_lies_about_sizes_cannot_widen_the_budget()
    {
        var body = new StringBuilder();
        var random = new Random(11);
        while (body.Length < 2 * 1024 * 1024) body.Append("<w:p><w:r><w:t>").Append(random.Next(1_000_000, 9_999_999)).Append("</w:t></w:r></w:p>");
        var zip = DocxKit.Docx(DocxKit.Wrap(body.ToString()));

        PatchSizes(zip, compressed: 0, uncompressed: 512);
        var result = Extract(zip, new AttachmentTextLimits { MaxXmlPartBytes = 64 * 1024 });

        Assert.True(result.Text.Length <= 64 * 1024, $"read {result.Text.Length} characters through a 64 KiB budget");
        Assert.NotEqual(DocxOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public void A_compressed_size_that_lies_does_not_defeat_the_ratio_check_or_the_budget()
    {
        var inflated = new byte[16 * 1024 * 1024];
        Array.Fill(inflated, (byte)'a');
        var zip = DocxKit.Zip(("word/document.xml", inflated));
        PatchSizes(zip, compressed: 0, uncompressed: 100);

        var result = Extract(zip, new AttachmentTextLimits { MaxXmlPartBytes = 1024 * 1024 });

        Assert.False(result.Ok && result.Text.Length > 1024 * 1024);
    }

    [Fact]
    public void The_total_inflated_budget_covers_every_part()
    {
        var one = DocxKit.Wrap("<w:p><w:r><w:t>" + new string('x', 400_000) + "</w:t></w:r></w:p>");
        var zip = DocxKit.Docx(one, ("word/footnotes.xml", one), ("word/endnotes.xml", one));

        var result = Extract(zip, new AttachmentTextLimits { MaxInflatedBytes = 700_000, MaxCompressionRatio = 100_000 }, max: 1_000_000);

        Assert.True(result.Ok);
        Assert.Equal(400_001, result.Text.Length);
    }

    [Fact]
    public void An_external_entity_is_refused_and_nothing_is_resolved()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
            + "<w:document " + DocxKit.Wml + "><w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>";

        var result = Extract(DocxKit.Docx(xml));

        Assert.Equal(DocxOutcome.Refused, result.Outcome);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void An_entity_expansion_bomb_is_refused_instantly()
    {
        var entities = new StringBuilder("<!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        for (var i = 2; i <= 9; i++)
            entities.Append($"<!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&lol{i - 1};", 10))}\">");
        entities.Append("]>");
        var xml = entities + "<w:document " + DocxKit.Wml + "><w:body><w:p><w:r><w:t>&lol9;</w:t></w:r></w:p></w:body></w:document>";

        var clock = Stopwatch.StartNew();
        var result = Extract(DocxKit.Docx(xml));

        Assert.Equal(DocxOutcome.Refused, result.Outcome);
        Assert.Empty(result.Text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_refused_footnote_part_does_not_discard_the_body()
    {
        var docx = DocxKit.Docx(
            DocxKit.Document("Kept body"),
            ("word/footnotes.xml", "<!DOCTYPE x [<!ENTITY a \"b\">]><w:footnotes " + DocxKit.Wml + "/>"));

        var result = Extract(docx);

        Assert.True(result.Ok);
        Assert.Equal("Kept body\n", result.Text);
    }

    [Fact]
    public void Malformed_xml_keeps_what_was_read_and_empty_malformed_xml_is_refused()
    {
        var half = Extract(DocxKit.Docx("<w:document " + DocxKit.Wml + "><w:body><w:p><w:r><w:t>Half</w:t></w:r></w:p><w:p><w:r><w:t"));
        Assert.True(half.Ok);
        Assert.Equal("Half\n", half.Text);

        var none = Extract(DocxKit.Docx("<w:docu"));
        Assert.False(none.Ok);
    }

    [Fact]
    public void A_utf16_document_part_is_refused()
    {
        var zip = DocxKit.Zip(("word/document.xml", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(DocxKit.Document("x"))).ToArray()));

        Assert.Equal(DocxOutcome.Refused, Extract(zip).Outcome);
    }

    [Fact]
    public void Corrupt_compressed_data_never_throws()
    {
        var body = new StringBuilder();
        var random = new Random(3);
        while (body.Length < 200_000) body.Append("<w:p><w:r><w:t>").Append(random.Next()).Append("</w:t></w:r></w:p>");
        var zip = DocxKit.Docx(DocxKit.Wrap(body.ToString()));

        for (var offset = 100; offset < zip.Length - 200; offset += 9_973)
        {
            var damaged = (byte[])zip.Clone();
            for (var i = 0; i < 32; i++) damaged[offset + i] ^= 0xFF;

            var result = Extract(damaged);
            Assert.True(Enum.IsDefined(result.Outcome));
        }
    }

    [Fact]
    public void Random_garbage_behind_a_valid_end_record_never_throws()
    {
        var random = new Random(99);
        var zip = DocxKit.Paragraphs("kept");

        for (var round = 0; round < 200; round++)
        {
            var damaged = (byte[])zip.Clone();
            for (var i = 0; i < 6; i++) damaged[random.Next(damaged.Length)] = (byte)random.Next(256);

            var result = Extract(damaged);
            Assert.True(Enum.IsDefined(result.Outcome));
        }
    }

    [Fact]
    public void A_deadline_already_past_stops_before_reading()
    {
        var result = Extract(DocxKit.Paragraphs("kept"), deadline: Stopwatch.GetTimestamp() - 1);

        Assert.Equal(DocxOutcome.TimedOut, result.Outcome);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void A_cancelled_token_aborts()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var zip = DocxKit.Paragraphs("kept");

        Assert.Throws<OperationCanceledException>(() =>
            DocxTextExtractor.Extract(zip, zip.Length, AttachmentTextLimits.Default, 1000, Forever, source.Token));
    }

    [Fact]
    public void The_character_budget_truncates_and_says_so()
    {
        var result = Extract(DocxKit.Paragraphs(new string('w', 5_000)), max: 100);

        Assert.True(result.Ok);
        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= 101);
    }

    [Fact]
    public void Entry_names_that_try_to_climb_out_are_never_opened_or_extracted()
    {
        var zip = DocxKit.Zip(
            ("../../word/document.xml", Encoding.UTF8.GetBytes(DocxKit.Document("escaped"))),
            ("/word/document.xml", Encoding.UTF8.GetBytes(DocxKit.Document("absolute"))),
            ("word/document.xml", Encoding.UTF8.GetBytes(DocxKit.Document("genuine"))));

        var result = Extract(zip);

        Assert.True(result.Ok);
        Assert.Equal("genuine\n", result.Text);
    }

    [Fact]
    public void Only_the_prefix_of_the_array_that_the_length_names_is_read()
    {
        var zip = DocxKit.Paragraphs("inside");
        var padded = zip.Concat(new byte[] { 9, 9, 9, 9 }).ToArray();

        var result = DocxTextExtractor.Extract(padded, zip.Length, AttachmentTextLimits.Default, 1000, Forever, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void Stored_and_deflated_entries_read_alike(CompressionLevel level)
    {
        var zip = DocxKit.Zip(level, ("word/document.xml", Encoding.UTF8.GetBytes(DocxKit.Document("same text"))));

        Assert.Equal("same text\n", Extract(zip).Text);
    }
}
