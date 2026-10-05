using System.Text;

namespace Mailcoded.Core.Parsing;

internal enum WordXmlScan
{
    Complete,
    Truncated,
    Malformed,
    Refused,
}

/// <summary>No XML parser on purpose: a DOCTYPE or ENTITY refuses the part, so no entity is ever expanded or fetched, and the cursor only moves forward.</summary>
internal static class WordXmlTextScanner
{
    private const int MaxEntityBytes = 12;
    private const int MaxFallbackDepth = 1_000;

    public static WordXmlScan Scan(ReadOnlySpan<byte> xml, StringBuilder output, int maxChars, CancellationToken ct = default)
    {
        var start = output.Length;
        var result = ScanCore(xml, output, start + Math.Max(maxChars, 0), ct);

        if (result == WordXmlScan.Refused) output.Length = start;
        return result;
    }

    private static WordXmlScan ScanCore(ReadOnlySpan<byte> xml, StringBuilder output, int limit, CancellationToken ct)
    {
        var i = 0;
        var n = xml.Length;

        if (n >= 3 && xml[0] == 0xEF && xml[1] == 0xBB && xml[2] == 0xBF) i = 3;
        else if (n >= 2 && ((xml[0] == 0xFF && xml[1] == 0xFE) || (xml[0] == 0xFE && xml[1] == 0xFF) || xml[0] == 0))
            return WordXmlScan.Refused;

        var inText = false;
        var fallback = 0;
        var tags = 0;

        while (i < n)
        {
            if ((++tags & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();

            var lt = xml[i..].IndexOf((byte)'<');
            var textEnd = lt < 0 ? n : i + lt;

            if (inText && fallback == 0 && AppendText(xml[i..textEnd], output, limit)) return WordXmlScan.Truncated;
            if (lt < 0) return WordXmlScan.Complete;

            i = textEnd;
            if (i + 1 >= n) return WordXmlScan.Malformed;

            var next = xml[i + 1];

            if (next == (byte)'!')
            {
                if (xml[i..].StartsWith("<!--"u8))
                {
                    var end = xml[(i + 4)..].IndexOf("-->"u8);
                    if (end < 0) return WordXmlScan.Malformed;
                    i += 4 + end + 3;
                    continue;
                }

                if (xml[i..].StartsWith("<![CDATA["u8))
                {
                    var end = xml[(i + 9)..].IndexOf("]]>"u8);
                    if (end < 0) return WordXmlScan.Malformed;
                    if (inText && fallback == 0 && AppendRaw(xml.Slice(i + 9, end), output, limit)) return WordXmlScan.Truncated;
                    i += 9 + end + 3;
                    continue;
                }

                return WordXmlScan.Refused;
            }

            if (next == (byte)'?')
            {
                var end = xml[(i + 2)..].IndexOf("?>"u8);
                if (end < 0) return WordXmlScan.Malformed;
                i += 2 + end + 2;
                continue;
            }

            var closing = next == (byte)'/';
            var nameStart = i + (closing ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < n && !IsNameEnd(xml[nameEnd])) nameEnd++;
            if (nameEnd >= n) return WordXmlScan.Malformed;

            var q = nameEnd;
            var hasAttributes = false;
            while (q < n && xml[q] != (byte)'>')
            {
                var b = xml[q];
                if (b is (byte)'"' or (byte)'\'')
                {
                    var close = xml[(q + 1)..].IndexOf(b);
                    if (close < 0) return WordXmlScan.Malformed;
                    hasAttributes = true;
                    q += close + 2;
                    continue;
                }

                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'/')) hasAttributes = true;
                q++;
            }

            if (q >= n) return WordXmlScan.Malformed;

            var selfClosing = !closing && xml[q - 1] == (byte)'/';
            var name = xml[nameStart..nameEnd];
            i = q + 1;

            if (closing)
            {
                if (name.SequenceEqual("mc:Fallback"u8))
                {
                    if (fallback > 0) fallback--;
                }
                else if (fallback == 0)
                {
                    if (name.SequenceEqual("w:t"u8)) inText = false;
                    else if (name.SequenceEqual("w:p"u8)) output.Append('\n');
                    else if (name.SequenceEqual("w:tc"u8)) output.Append('\t');
                }

                if (output.Length >= limit) return WordXmlScan.Truncated;
                continue;
            }

            if (name.SequenceEqual("mc:Fallback"u8))
            {
                if (!selfClosing && fallback < MaxFallbackDepth) fallback++;
                continue;
            }

            if (fallback > 0) continue;

            if (name.SequenceEqual("w:t"u8)) inText = !selfClosing;
            else if (name.SequenceEqual("w:tab"u8) && !hasAttributes) output.Append('\t');
            else if (name.SequenceEqual("w:br"u8) || name.SequenceEqual("w:cr"u8)) output.Append('\n');
            else if (name.SequenceEqual("w:noBreakHyphen"u8)) output.Append('-');

            if (output.Length >= limit) return WordXmlScan.Truncated;
        }

        return WordXmlScan.Complete;
    }

    private static bool IsNameEnd(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'/' or (byte)'>';

    private static bool AppendRaw(ReadOnlySpan<byte> utf8, StringBuilder output, int limit)
    {
        var room = limit - output.Length;
        if (room <= 0) return true;

        if (utf8.Length > room) utf8 = utf8[..room];
        output.Append(Encoding.UTF8.GetString(utf8));
        return output.Length >= limit;
    }

    private static bool AppendText(ReadOnlySpan<byte> segment, StringBuilder output, int limit)
    {
        while (!segment.IsEmpty)
        {
            var amp = segment.IndexOf((byte)'&');
            if (amp < 0) return AppendRaw(segment, output, limit);

            if (AppendRaw(segment[..amp], output, limit)) return true;
            segment = segment[amp..];

            var semicolon = segment[..Math.Min(segment.Length, MaxEntityBytes)].IndexOf((byte)';');
            if (semicolon < 0)
            {
                segment = segment[1..];
                continue;
            }

            AppendEntity(segment.Slice(1, semicolon - 1), output);
            segment = segment[(semicolon + 1)..];
            if (output.Length >= limit) return true;
        }

        return output.Length >= limit;
    }

    private static void AppendEntity(ReadOnlySpan<byte> name, StringBuilder output)
    {
        if (name.SequenceEqual("amp"u8)) output.Append('&');
        else if (name.SequenceEqual("lt"u8)) output.Append('<');
        else if (name.SequenceEqual("gt"u8)) output.Append('>');
        else if (name.SequenceEqual("quot"u8)) output.Append('"');
        else if (name.SequenceEqual("apos"u8)) output.Append('\'');
        else if (name.Length > 1 && name[0] == (byte)'#') AppendCodePoint(name[1..], output);
    }

    private static void AppendCodePoint(ReadOnlySpan<byte> digits, StringBuilder output)
    {
        var hex = !digits.IsEmpty && digits[0] is (byte)'x' or (byte)'X';
        if (hex) digits = digits[1..];
        if (digits.IsEmpty) return;

        var value = 0;
        foreach (var d in digits)
        {
            int digit;
            if (d is >= (byte)'0' and <= (byte)'9') digit = d - '0';
            else if (hex && d is >= (byte)'a' and <= (byte)'f') digit = d - 'a' + 10;
            else if (hex && d is >= (byte)'A' and <= (byte)'F') digit = d - 'A' + 10;
            else return;

            value = (value * (hex ? 16 : 10)) + digit;
            if (value > 0x10FFFF) return;
        }

        if (!Rune.TryCreate(value, out var rune)) return;
        if (value < 0x20 && value is not (0x09 or 0x0A or 0x0D)) return;
        output.Append(rune.ToString());
    }
}
