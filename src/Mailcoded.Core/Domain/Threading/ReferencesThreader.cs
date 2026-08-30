using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Threading;

/// <summary>Native thread id, else the References root through the lookup, else subject + participant.</summary>
public sealed class ReferencesThreader : IThreader
{
    public static readonly ReferencesThreader Instance = new();

    public const string NativePrefix = "n:";
    public const string RootPrefix = "m:";
    public const string SubjectPrefix = "s:";
    public const string SyntheticPrefix = "d:";

    private const int MaxNativeIdLength = 128;
    private const int MaxRootIdLength = 512;

    public ThreadKey Resolve(ThreadCandidate candidate, Func<MessageId, ThreadKey?> lookup)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(lookup);

        if (NormalizeNativeId(candidate.NativeThreadId) is { } native)
            return ThreadKey.Create(NativePrefix + native);

        foreach (var ancestor in Ancestors(candidate))
            if (lookup(ancestor) is { } known && !string.IsNullOrWhiteSpace(known.Value))
                return known;

        foreach (var ancestor in Ancestors(candidate))
            return ThreadKey.Create(RootPrefix + Clamp(ancestor.Value, MaxRootIdLength));

        if (candidate.MessageId is { } own && IsUsable(own))
            return ThreadKey.Create(RootPrefix + Clamp(own.Value, MaxRootIdLength));

        var subject = SubjectNormalizer.Normalize(candidate.Subject);
        var participant = NormalizeParticipant(candidate.FromAddress);

        // No Message-ID and no references: the key must still be stable for the same inputs.
        return subject.Length > 0
            ? ThreadKey.Create(SubjectPrefix + Fingerprint(subject.ToLowerInvariant(), participant))
            : ThreadKey.Create(SyntheticPrefix + Fingerprint(
                participant,
                candidate.DateUtc.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Ancestors root-first: References in order, then In-Reply-To.</summary>
    private static IEnumerable<MessageId> Ancestors(ThreadCandidate candidate)
    {
        foreach (var r in candidate.References)
            if (IsUsable(r)) yield return r;

        if (candidate.InReplyTo is { } parent && IsUsable(parent)) yield return parent;
    }

    private static bool IsUsable(MessageId id) => !string.IsNullOrWhiteSpace(id.Value);

    /// <summary>Anything unexpected returns null so the candidate falls through to normal threading.</summary>
    private static string? NormalizeNativeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length > MaxNativeIdLength) return null;
        foreach (var c in s)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '@')) return null;
        return s;
    }

    private static string NormalizeParticipant(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return string.Empty;
        var s = from.Trim();

        var lt = s.LastIndexOf('<');
        if (lt >= 0)
        {
            var gt = s.IndexOf('>', lt + 1);
            if (gt > lt + 1) s = s[(lt + 1)..gt];
        }

        return s.Trim().ToLowerInvariant();
    }

    private static string Clamp(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Fingerprint(string a, string b)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(a, " ", b));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash[..16]);
    }
}

/// <summary>Language-agnostic reply/forward prefix stripping and whitespace collapsing.</summary>
public static class SubjectNormalizer
{
    private static readonly string[] Prefixes =
    [
        "antwort", "betreff", "doorst", "antw", "odp", "fwd", "res", "ref", "enc",
        "ynt", "ilt", "rif", "fw", "wg", "sv", "vs", "vl", "tr", "aw", "re", "r", "i",
        "回复", "回覆", "答复", "答覆",
        "转发", "轉發", "转寄", "轉寄",
    ];

    /// <summary>Case is preserved; callers keying on the result lower-case it themselves.</summary>
    public static string Normalize(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return string.Empty;

        var span = subject.AsSpan();
        var start = StripPrefixes(span);
        return CollapseWhitespace(span[start..]);
    }

    // The cursor only ever advances, so "Re:" repeated ten thousand times costs one linear pass.
    private static int StripPrefixes(ReadOnlySpan<char> s)
    {
        var pos = 0;
        while (pos < s.Length)
        {
            var at = SkipIgnorable(s, pos);
            if (at >= s.Length) break;

            var next = TryConsumePrefix(s, at);
            if (next <= pos) break;
            pos = next;
        }
        return pos;
    }

    private static int TryConsumePrefix(ReadOnlySpan<char> s, int start)
    {
        foreach (var token in Prefixes)
        {
            if (start + token.Length > s.Length) continue;
            if (!s.Slice(start, token.Length).Equals(token, StringComparison.OrdinalIgnoreCase)) continue;

            var p = SkipIgnorable(s, start + token.Length);

            // Outlook repetition counters: Re[2]: / Re(2):
            if (p < s.Length && s[p] is '[' or '(')
            {
                var close = s[p] == '[' ? ']' : ')';
                var q = p + 1;
                while (q < s.Length && char.IsAsciiDigit(s[q])) q++;
                if (q > p + 1 && q < s.Length && s[q] == close) p = SkipIgnorable(s, q + 1);
            }

            if (p < s.Length && (s[p] == ':' || s[p] == '\uFF1A')) return p + 1;
        }
        return -1;
    }

    private static int SkipIgnorable(ReadOnlySpan<char> s, int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || IsZeroWidth(s[i]))) i++;
        return i;
    }

    private static bool IsZeroWidth(char c) => c == '\uFEFF' || (c >= '\u200B' && c <= '\u200F');

    private static string CollapseWhitespace(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder(s.Length);
        var gap = false;

        foreach (var c in s)
        {
            if (IsZeroWidth(c)) continue;
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0) gap = true;
                continue;
            }
            if (gap) { sb.Append(' '); gap = false; }
            sb.Append(c);
        }

        return sb.ToString();
    }
}
