namespace Mailcoded.Core.Domain.Signals;

/// <summary>Pure derivations over header strings and Content-Type facts; the sender controls every input, so these sort mail and never vouch for it.</summary>
public static class MessageSignals
{
    private static readonly string[] RobotLocalParts =
    [
        "noreply", "no-reply", "no_reply", "no.reply",
        "donotreply", "do-not-reply", "do_not_reply", "do.not.reply",
        "mailer-daemon", "mailerdaemon", "postmaster", "bounce", "bounces",
    ];

    /// <summary>True on any automation signal; false only when the headers were available and none matched; otherwise null.</summary>
    public static bool? IsAutomated(SignalHeaders? headers, string? from, ContentShape? top)
    {
        if (headers is not null && HasAutomationHeader(headers)) return true;
        if (top is { } shape && IsAutomatedReport(shape)) return true;
        if (IsRobotSender(from)) return true;

        return headers is null ? null : false;
    }

    public static bool HasAutomationHeader(SignalHeaders headers) =>
        headers.ListId is not null
        || headers.ListUnsubscribe is not null
        || headers.XAutoreply is not null
        || headers.XAutorespond is not null
        || headers.XFailedRecipients is not null
        || IsBulkPrecedence(headers.Precedence)
        || IsAutoSubmitted(headers.AutoSubmitted);

    /// <summary>RFC 3834: any value other than "no" marks the message as machine-generated.</summary>
    public static bool IsAutoSubmitted(string? value)
    {
        foreach (var token in Tokens(value))
            if (!token.Equals("no", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static bool IsBulkPrecedence(string? value)
    {
        foreach (var token in Tokens(value))
        {
            if (token.Equals("bulk", StringComparison.OrdinalIgnoreCase)
                || token.Equals("list", StringComparison.OrdinalIgnoreCase)
                || token.Equals("junk", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Delivery status (RFC 3464), disposition notification (RFC 8098) and abuse feedback (RFC 5965) reports.</summary>
    public static bool IsAutomatedReport(ContentShape shape)
    {
        if (!shape.Is("multipart", "report") || shape.ReportType is not { } type) return false;

        var trimmed = type.Trim();
        return trimmed.Equals("delivery-status", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("disposition-notification", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("feedback-report", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRobotSender(string? from)
    {
        var local = FirstLocalPart(from);
        return local is not null && IsRobotLocalPart(local);
    }

    public static bool IsRobotLocalPart(string localPart) => HasLocalPartPrefix(localPart, RobotLocalParts);

    /// <summary>True when the local part, minus any +tag, is one of the names or starts with one followed by <c>- _ . =</c>.</summary>
    internal static bool HasLocalPartPrefix(string localPart, string[] names)
    {
        var local = localPart.Trim().Trim('"').ToLowerInvariant();

        var plus = local.IndexOf('+');
        if (plus >= 0) local = local[..plus];

        foreach (var name in names)
        {
            if (!local.StartsWith(name, StringComparison.Ordinal)) continue;
            if (local.Length == name.Length || local[name.Length] is '-' or '_' or '.' or '=') return true;
        }

        return false;
    }

    /// <summary>RFC 3156 multipart/encrypted, or RFC 8551 enveloped / authEnveloped S/MIME.</summary>
    public static bool IsEncrypted(ContentShape shape)
    {
        if (shape.Is("multipart", "encrypted")) return true;

        if (!shape.Is("application", "pkcs7-mime") && !shape.Is("application", "x-pkcs7-mime")) return false;

        var smime = shape.SmimeType?.Trim();
        return smime is not null
            && (smime.Equals("enveloped-data", StringComparison.OrdinalIgnoreCase)
                || smime.Equals("authEnveloped-data", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An ASCII-armored PGP message: a BEGIN line and a later END line, both at line starts.</summary>
    public static bool ContainsPgpArmor(ReadOnlySpan<char> text)
    {
        const string Begin = "-----BEGIN PGP MESSAGE";
        const string End = "-----END PGP MESSAGE";

        var offset = 0;
        while (offset < text.Length)
        {
            var found = text[offset..].IndexOf(Begin, StringComparison.Ordinal);
            if (found < 0) return false;

            var start = offset + found;
            var afterBegin = start + Begin.Length;
            offset = afterBegin;

            var atLineStart = start == 0 || text[start - 1] == '\n';
            if (!atLineStart || !ClosesArmorLine(text[afterBegin..])) continue;

            // No END after this BEGIN means no END after any later one, so the scan stays linear.
            var from = afterBegin;
            while (from < text.Length)
            {
                var end = text[from..].IndexOf(End, StringComparison.Ordinal);
                if (end < 0) return false;

                var endStart = from + end;
                var afterEnd = endStart + End.Length;
                if (text[endStart - 1] == '\n' && ClosesArmorLine(text[afterEnd..])) return true;
                from = afterEnd;
            }

            return false;
        }

        return false;
    }

    private static bool ClosesArmorLine(ReadOnlySpan<char> rest) =>
        rest.StartsWith("-----", StringComparison.Ordinal) || rest.StartsWith(", PART", StringComparison.Ordinal);

    /// <summary>The local part of the first address in a From display string, or null when there is none.</summary>
    public static string? FirstLocalPart(string? from)
    {
        var spec = FirstAddrSpec(from);
        if (spec is null) return null;

        var at = spec.LastIndexOf('@');
        return at <= 0 ? null : spec[..at];
    }

    /// <summary>The lower-cased domain of the first address in a From display string, without a trailing dot; null when there is none.</summary>
    public static string? FirstDomain(string? from)
    {
        var spec = FirstAddrSpec(from);
        if (spec is null) return null;

        var at = spec.LastIndexOf('@');
        if (at <= 0 || at == spec.Length - 1) return null;

        var domain = spec[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
        return domain.Length == 0 ? null : domain;
    }

    private static string? FirstAddrSpec(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;

        var quoted = false;
        var open = -1;

        for (var i = 0; i < from.Length; i++)
        {
            var c = from[i];

            if (quoted)
            {
                if (c == '\\') i++;
                else if (c == '"') quoted = false;
                continue;
            }

            if (c == '"') quoted = true;
            else if (c == '<') { open = i; break; }
            else if (c == ',') break;
        }

        if (quoted) return null;

        if (open >= 0)
        {
            var close = from.IndexOf('>', open + 1);
            return close < 0 ? null : from[(open + 1)..close].Trim();
        }

        var comma = from.IndexOf(',');
        var bare = (comma < 0 ? from : from[..comma]).Trim();
        return bare.Contains('@') ? bare : null;
    }

    private static IEnumerable<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        foreach (var part in StripComments(value).Split(','))
        {
            var semicolon = part.IndexOf(';');
            var token = (semicolon < 0 ? part : part[..semicolon]).Trim();
            if (token.Length > 0) yield return token;
        }
    }

    private static string StripComments(string value)
    {
        if (value.IndexOf('(') < 0) return value;

        var builder = new System.Text.StringBuilder(value.Length);
        var depth = 0;

        foreach (var c in value)
        {
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if (depth == 0) builder.Append(c);
        }

        return builder.ToString();
    }
}
