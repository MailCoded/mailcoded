using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Mailcoded.Core.Parsing;

public enum DocxOutcome
{
    Extracted,
    NotAZip,
    TooManyEntries,
    NoDocument,
    TooLarge,
    CompressionRatio,
    Refused,
    Malformed,
    TimedOut,
}

public sealed record DocxText(DocxOutcome Outcome, string Text, bool Truncated)
{
    public bool Ok => Outcome == DocxOutcome.Extracted;
}

/// <summary>Text of a hostile .docx: entry count checked before <see cref="ZipArchive"/> enumerates, three named parts opened, every byte counted as read.</summary>
public static class DocxTextExtractor
{
    private static readonly string[] Parts = ["word/document.xml", "word/footnotes.xml", "word/endnotes.xml"];

    private const int ReadChunk = 81_920;
    private const int EndRecordLength = 22;
    private const int MaxCommentLength = ushort.MaxValue;

    public static DocxText Extract(
        byte[] docx,
        int length,
        AttachmentTextLimits limits,
        int maxChars,
        long deadlineTimestamp,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(docx);
        ArgumentNullException.ThrowIfNull(limits);

        var entries = DeclaredEntryCount(docx.AsSpan(0, length));
        if (entries < 0) return new DocxText(DocxOutcome.NotAZip, string.Empty, false);
        if (entries > limits.MaxZipEntries) return new DocxText(DocxOutcome.TooManyEntries, string.Empty, false);

        try
        {
            using var stream = new MemoryStream(docx, 0, length, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return ReadParts(archive, limits, maxChars, deadlineTimestamp, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            return new DocxText(DocxOutcome.Malformed, string.Empty, false);
        }
    }

    // A Zip64 marker counts as past any limit: Word writes none for a document that fits in memory.
    internal static int DeclaredEntryCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < EndRecordLength) return -1;

        var floor = Math.Max(0, data.Length - EndRecordLength - MaxCommentLength);
        for (var at = data.Length - EndRecordLength; at >= floor; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) != 0x06054B50u) continue;

            var total = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 10)..]);
            return total == ushort.MaxValue ? int.MaxValue : total;
        }

        return -1;
    }

    private static DocxText ReadParts(
        ZipArchive archive,
        AttachmentTextLimits limits,
        int maxChars,
        long deadlineTimestamp,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var inflated = 0L;
        var truncated = false;
        var sawDocument = false;
        var refusal = DocxOutcome.Extracted;

        foreach (var name in Parts)
        {
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadlineTimestamp) return Finish(DocxOutcome.TimedOut);

            var entry = archive.GetEntry(name);
            if (entry is null) continue;

            if (name == Parts[0]) sawDocument = true;

            if (entry.Length > limits.MaxXmlPartBytes) { refusal = DocxOutcome.TooLarge; continue; }

            var stored = Math.Max(entry.CompressedLength, 1);
            if (entry.Length / stored > limits.MaxCompressionRatio) { refusal = DocxOutcome.CompressionRatio; continue; }

            var budget = (int)Math.Min(limits.MaxXmlPartBytes, limits.MaxInflatedBytes - inflated);
            if (budget <= 0) { refusal = DocxOutcome.TooLarge; continue; }

            var read = ReadBounded(entry, budget, deadlineTimestamp, ct, out var outcome);
            if (outcome != DocxOutcome.Extracted)
            {
                if (outcome == DocxOutcome.TimedOut) return Finish(outcome);
                refusal = outcome;
                continue;
            }

            inflated += read.Length;

            var room = maxChars - text.Length;
            if (room <= 0) { truncated = true; break; }

            var scan = WordXmlTextScanner.Scan(read.AsSpan(), text, room, ct);
            if (scan == WordXmlScan.Refused) { refusal = DocxOutcome.Refused; continue; }
            if (scan == WordXmlScan.Truncated) truncated = true;
            if (scan == WordXmlScan.Malformed && name == Parts[0] && text.Length == 0) refusal = DocxOutcome.Malformed;
        }

        if (!sawDocument) return Finish(DocxOutcome.NoDocument);
        if (text.Length == 0 && refusal != DocxOutcome.Extracted) return Finish(refusal);

        return new DocxText(DocxOutcome.Extracted, text.ToString(), truncated);

        DocxText Finish(DocxOutcome outcome) => new(outcome, string.Empty, false);
    }

    private static byte[] ReadBounded(
        ZipArchiveEntry entry,
        int budget,
        long deadlineTimestamp,
        CancellationToken ct,
        out DocxOutcome outcome)
    {
        outcome = DocxOutcome.Extracted;

        using var source = entry.Open();
        using var sink = new MemoryStream(Math.Min(budget, 1 << 20));
        var chunk = new byte[ReadChunk];
        var total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadlineTimestamp)
            {
                outcome = DocxOutcome.TimedOut;
                return [];
            }

            var want = Math.Min(chunk.Length, budget - total + 1);
            var got = source.Read(chunk, 0, want);
            if (got <= 0) break;

            total += got;
            if (total > budget)
            {
                outcome = DocxOutcome.TooLarge;
                return [];
            }

            sink.Write(chunk, 0, got);
        }

        return sink.ToArray();
    }
}
