using System.Diagnostics;
using System.Text;
using MimeKit;

namespace Mailcoded.Core.Parsing;

internal sealed class AttachmentTextExtractor(AttachmentTextLimits limits)
{
    private const int BinarySniffBytes = 4_096;

    public List<ParsedAttachmentText> Extract(
        IReadOnlyList<MimeEntity> entities,
        IReadOnlyList<ParsedAttachment> attachments,
        List<string> warnings,
        CancellationToken ct)
    {
        var results = new List<ParsedAttachmentText>();
        var deadline = Stopwatch.GetTimestamp() + (long)(limits.TimeBudget.TotalSeconds * Stopwatch.Frequency);
        var attempted = 0;
        var decoded = 0L;
        var chars = 0;

        for (var i = 0; i < attachments.Count && i < entities.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var attachment = attachments[i];
            var kind = AttachmentTextClassifier.Classify(attachment.MimeType, attachment.FileName);
            if (kind == AttachmentTextKind.None || entities[i] is not MimePart { Content: not null } part) continue;

            if (attempted >= limits.MaxAttachments) { Skip(warnings, "limit"); break; }
            if (decoded >= limits.MaxDecodedBytesPerMessage) { Skip(warnings, "decoded-bytes"); break; }
            if (chars >= limits.MaxCharsPerMessage) { Skip(warnings, "text-limit"); break; }
            if (Stopwatch.GetTimestamp() >= deadline) { Skip(warnings, "time-budget"); break; }

            attempted++;

            if (kind == AttachmentTextKind.Docx && attachment.Size > limits.MaxDocxBytes)
            {
                Skip(warnings, "docx-too-large");
                continue;
            }

            try
            {
                var room = Math.Min(limits.MaxCharsPerAttachment, limits.MaxCharsPerMessage - chars);
                var entry = ExtractOne(part, attachment, kind, room, deadline, warnings, ref decoded, ct);
                if (entry is null) continue;

                chars += entry.Text.Length;
                results.Add(entry);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Skip(warnings, "unreadable:" + ex.GetType().Name);
            }
        }

        return results;
    }

    private ParsedAttachmentText? ExtractOne(
        MimePart part,
        ParsedAttachment attachment,
        AttachmentTextKind kind,
        int maxChars,
        long deadline,
        List<string> warnings,
        ref long decoded,
        CancellationToken ct)
    {
        var cap = kind == AttachmentTextKind.Docx ? limits.MaxDocxBytes : limits.MaxTextBytes;
        cap = (int)Math.Min(cap, Math.Max(limits.MaxDecodedBytesPerMessage - decoded, 0));

        var buffer = ReadBounded(part.Content!, cap, ct, out var length, out var overflowed);
        decoded += length;

        string text;
        var truncated = overflowed;

        if (kind == AttachmentTextKind.Docx)
        {
            if (overflowed) { Skip(warnings, "docx-too-large"); return null; }

            var docx = DocxTextExtractor.Extract(buffer, length, limits, maxChars, deadline, ct);
            if (!docx.Ok)
            {
                Skip(warnings, "docx-" + docx.Outcome.ToString().ToLowerInvariant());
                return null;
            }

            text = PlainText.Normalize(docx.Text, maxChars);
            truncated |= docx.Truncated;
        }
        else
        {
            var raw = DecodeText(buffer, length, part.ContentType.Charset);
            if (raw is null) { Skip(warnings, "binary"); return null; }

            text = kind == AttachmentTextKind.Html
                ? HtmlToText.Convert(raw, maxChars)
                : PlainText.Normalize(raw, maxChars);
            truncated |= raw.Length > maxChars;
        }

        if (text.Length == 0) return null;

        return new ParsedAttachmentText
        {
            Index = attachment.Index,
            MimeType = attachment.MimeType,
            FileName = attachment.FileName,
            Text = text,
            Truncated = truncated,
        };
    }

    private static byte[] ReadBounded(IMimeContent content, int cap, CancellationToken ct, out int length, out bool overflowed)
    {
        using var source = content.Open();
        var buffer = new byte[Math.Min(Math.Max(cap, 0) + 1, 64 * 1024)];
        var total = 0;
        overflowed = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (total == buffer.Length)
            {
                if (total > cap) break;
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, (long)cap + 1));
            }

            var read = source.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
            if (total > cap) break;
        }

        if (total > cap)
        {
            overflowed = true;
            total = cap;
        }

        length = total;
        return buffer;
    }

    internal static string? DecodeText(byte[] buffer, int length, string? charset)
    {
        if (length == 0) return string.Empty;

        var span = buffer.AsSpan(0, length);
        Encoding encoding;
        var skip = 0;

        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            encoding = Encoding.UTF8;
            skip = 3;
        }
        else if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            encoding = Encoding.Unicode;
            skip = 2;
        }
        else if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            encoding = Encoding.BigEndianUnicode;
            skip = 2;
        }
        else
        {
            if (span[..Math.Min(span.Length, BinarySniffBytes)].Contains((byte)0)) return null;

            if (!string.IsNullOrWhiteSpace(charset)) encoding = MimeCharsets.Resolve(charset);
            else
            {
                try
                {
                    return CheckText(new UTF8Encoding(false, true).GetString(span));
                }
                catch (DecoderFallbackException)
                {
                    encoding = MimeCharsets.FallbackEncoding;
                }
            }
        }

        return CheckText(encoding.GetString(span[skip..]));
    }

    private static string? CheckText(string text)
    {
        var controls = 0;
        foreach (var c in text)
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r' or '\f'))
                controls++;

        return controls * 20 > text.Length ? null : text;
    }

    private static void Skip(List<string> warnings, string reason)
    {
        var warning = "attachment-text-skipped:" + reason;
        if (warnings.Count < 32 && !warnings.Contains(warning)) warnings.Add(warning);
    }
}
