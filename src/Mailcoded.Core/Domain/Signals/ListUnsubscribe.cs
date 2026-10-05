using System.Globalization;
using System.Text;
using System.Text.Unicode;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Signals;

/// <summary>The one recipient of a mailto unsubscribe, with the two hfields that are honoured.</summary>
public sealed record MailtoUnsubscribe(string Address, string? Subject, string? Body);

/// <summary>What the List-Unsubscribe headers offer. Sender-controlled, so never a trust signal.</summary>
public sealed record UnsubscribeOptions
{
    public MailtoUnsubscribe? Mailto { get; init; }

    /// <summary>http and https URLs, for a human to open. mailcoded never fetches them.</summary>
    public IReadOnlyList<string> Web { get; init; } = [];

    /// <summary>The sender advertises RFC 8058 one-click POST. Informational: mailcoded refuses to perform it.</summary>
    public bool OneClick { get; init; }

    public bool IsEmpty => Mailto is null && Web.Count == 0;

    public static UnsubscribeOptions None { get; } = new();
}

/// <summary>RFC 2369, RFC 8058 and RFC 6068 parsing, strict because every byte is attacker input.</summary>
public static class ListUnsubscribeParser
{
    public const int MaxEntries = 16;
    public const int MaxUriChars = 1_000;
    public const int MaxWebUrls = 4;
    public const int MaxSubjectChars = 300;
    public const int MaxBodyChars = 600;
    public const int MaxHeaderFields = 16;

    public const string WebNotice =
        "Sender-supplied links, untrusted. mailcoded never opens or POSTs to them; do not open them from an agent.";

    private const string OneClickValue = "List-Unsubscribe=One-Click";
    private const string MailtoScheme = "mailto:";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static UnsubscribeOptions Parse(string? listUnsubscribe, string? listUnsubscribePost)
    {
        if (string.IsNullOrWhiteSpace(listUnsubscribe)) return UnsubscribeOptions.None;

        MailtoUnsubscribe? mailto = null;
        List<string>? web = null;
        var https = false;
        var entries = 0;

        for (var i = 0; i < listUnsubscribe.Length && entries < MaxEntries; i++)
        {
            var c = listUnsubscribe[i];

            if (c == '(')
            {
                i = SkipComment(listUnsubscribe, i);
                if (i < 0) break;
                continue;
            }

            if (c != '<') continue;

            var close = listUnsubscribe.IndexOf('>', i + 1);
            if (close < 0) break;

            entries++;
            var uri = listUnsubscribe.AsSpan(i + 1, close - i - 1).Trim();
            i = close;

            if (uri.Length == 0 || uri.Length > MaxUriChars || !IsUriText(uri)) continue;

            if (uri.StartsWith(MailtoScheme, StringComparison.OrdinalIgnoreCase))
            {
                if (mailto is null && TryParseMailto(uri.ToString(), out var parsed)) mailto = parsed;
                continue;
            }

            if (web is { Count: >= MaxWebUrls } || !TryNormalizeWeb(uri, out var isHttps)) continue;

            var url = uri.ToString();
            web ??= [];
            if (web.Contains(url, StringComparer.Ordinal)) continue;

            web.Add(url);
            https |= isHttps;
        }

        var oneClick = https
            && listUnsubscribePost is not null
            && string.Equals(listUnsubscribePost.Trim(), OneClickValue, StringComparison.OrdinalIgnoreCase);

        return new UnsubscribeOptions { Mailto = mailto, Web = web is null ? [] : web, OneClick = oneClick };
    }

    /// <summary>Only the path address, subject and body count; a malformed or hostile URI is rejected whole.</summary>
    public static bool TryParseMailto(string uri, out MailtoUnsubscribe result)
    {
        result = null!;
        if (uri is null || uri.Length > MaxUriChars) return false;
        if (!uri.StartsWith(MailtoScheme, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = uri.AsSpan(MailtoScheme.Length);
        if (!IsUriText(rest) || rest.Contains('#')) return false;

        var question = rest.IndexOf('?');
        var path = question < 0 ? rest : rest[..question];
        var query = question < 0 ? ReadOnlySpan<char>.Empty : rest[(question + 1)..];

        if (!TryDecode(path, out var decodedPath) || !TryAddress(decodedPath, out var address)) return false;

        string? subject = null;
        string? body = null;
        var seenSubject = false;
        var seenBody = false;
        var fields = 0;

        while (!query.IsEmpty && fields < MaxHeaderFields)
        {
            fields++;
            var amp = query.IndexOf('&');
            var pair = amp < 0 ? query : query[..amp];
            query = amp < 0 ? ReadOnlySpan<char>.Empty : query[(amp + 1)..];

            var equals = pair.IndexOf('=');
            var rawName = equals < 0 ? pair : pair[..equals];
            var rawValue = equals < 0 ? ReadOnlySpan<char>.Empty : pair[(equals + 1)..];

            if (!TryDecode(rawName, out var name)) continue;

            var isSubject = string.Equals(name, "subject", StringComparison.OrdinalIgnoreCase);
            var isBody = string.Equals(name, "body", StringComparison.OrdinalIgnoreCase);
            if (!isSubject && !isBody) continue;

            if (isSubject ? seenSubject : seenBody) continue;
            if (!TryDecode(rawValue, out var value)) return false;

            if (isSubject)
            {
                seenSubject = true;
                if (!IsCleanText(value, allowLineBreaks: false) || value.Length > MaxSubjectChars) return false;
                subject = value.Length == 0 ? null : value;
            }
            else
            {
                seenBody = true;
                value = value.Replace("\r\n", "\n", StringComparison.Ordinal);
                if (!IsCleanText(value, allowLineBreaks: true) || value.Length > MaxBodyChars) return false;
                body = value.Length == 0 ? null : value;
            }
        }

        result = new MailtoUnsubscribe(address, subject, body);
        return true;
    }

    private static int SkipComment(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\\':
                    i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    private static bool IsUriText(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is <= ' ' or > '~') return false;
            if (c is '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}') return false;
        }

        return true;
    }

    private static bool TryNormalizeWeb(ReadOnlySpan<char> uri, out bool https)
    {
        https = false;

        ReadOnlySpan<char> rest;
        if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            https = true;
            rest = uri["https://".Length..];
        }
        else if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            rest = uri["http://".Length..];
        }
        else
        {
            return false;
        }

        var end = rest.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        if (authority.IsEmpty) return false;

        // user@host is the classic way to make one site read as another.
        if (authority.Contains('@') || authority.Contains('%') || authority.Contains('[')) return false;

        var host = authority;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            var port = authority[(colon + 1)..];
            if (port.IsEmpty || port.Length > 5) return false;
            foreach (var c in port) if (c is < '0' or > '9') return false;
            if (int.Parse(port, CultureInfo.InvariantCulture) > ushort.MaxValue) return false;
            host = authority[..colon];
        }

        if (host.IsEmpty || host[0] is '.' or '-' || host[^1] is '.' or '-') return false;

        var previousDot = false;
        foreach (var c in host)
        {
            var isDot = c == '.';
            if (isDot && previousDot) return false;
            previousDot = isDot;
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) return false;
        }

        return true;
    }

    private static bool TryDecode(ReadOnlySpan<char> encoded, out string decoded)
    {
        decoded = string.Empty;
        if (encoded.IsEmpty) return true;

        var bytes = new byte[encoded.Length];
        var count = 0;

        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c != '%')
            {
                bytes[count++] = (byte)c;
                continue;
            }

            if (i + 2 >= encoded.Length) return false;
            if (!char.IsAsciiHexDigit(encoded[i + 1]) || !char.IsAsciiHexDigit(encoded[i + 2])) return false;

            bytes[count++] = (byte)((HexValue(encoded[i + 1]) << 4) | HexValue(encoded[i + 2]));
            i += 2;
        }

        var span = bytes.AsSpan(0, count);
        if (!Utf8.IsValid(span)) return false;

        decoded = StrictUtf8.GetString(span);
        return true;
    }

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    private static bool IsCleanText(string text, bool allowLineBreaks)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (allowLineBreaks && rune.Value is '\n' or '\t') continue;

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAddress(string decoded, out string address)
    {
        address = string.Empty;
        if (decoded.Length == 0 || decoded.Length > EmailAddress.MaxLength) return false;

        var at = decoded.IndexOf('@');
        if (at <= 0 || at != decoded.LastIndexOf('@')) return false;

        var local = decoded.AsSpan(0, at);
        if (local.Length > EmailAddress.MaxLocalPartLength || local[0] == '.' || local[^1] == '.') return false;

        var previousDot = false;
        foreach (var rune in local.EnumerateRunes())
        {
            var isDot = rune.Value == '.';
            if (isDot && previousDot) return false;
            previousDot = isDot;

            if (rune.Value < 0x80)
            {
                if (!(char.IsAsciiLetterOrDigit((char)rune.Value) || isDot || "!#$%&'*+/=?^_`{|}~-".Contains((char)rune.Value))) return false;
            }
            else if (!Rune.IsLetterOrDigit(rune))
            {
                return false;
            }
        }

        if (!EmailAddress.TryParse(decoded, out var parsed)) return false;

        address = parsed.Value;
        return true;
    }
}

/// <summary>Stored as the sanitized List-Unsubscribe value, a line feed, then the Post value; parsed on read.</summary>
public static class UnsubscribeStorage
{
    private const char Separator = '\n';

    /// <summary>Null when the headers were not fetched; empty when they were and offered nothing.</summary>
    public static string? Encode(SignalHeaders? headers)
    {
        if (headers is null) return null;
        if (string.IsNullOrWhiteSpace(headers.ListUnsubscribe)) return string.Empty;

        return string.IsNullOrWhiteSpace(headers.ListUnsubscribePost)
            ? headers.ListUnsubscribe
            : headers.ListUnsubscribe + Separator + headers.ListUnsubscribePost;
    }

    public static UnsubscribeOptions? Decode(string? stored)
    {
        if (stored is null) return null;
        if (stored.Length == 0) return UnsubscribeOptions.None;

        var split = stored.IndexOf(Separator);
        return split < 0
            ? ListUnsubscribeParser.Parse(stored, null)
            : ListUnsubscribeParser.Parse(stored[..split], stored[(split + 1)..]);
    }
}
