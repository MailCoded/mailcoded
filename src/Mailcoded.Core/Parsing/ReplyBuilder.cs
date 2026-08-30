using System.Globalization;
using System.Text;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// Everything a reply needs that derives from the message being replied to: the quoted body, the
/// subject, and the In-Reply-To / References chain required for the thread to survive on the
/// recipient's side. Pure string work -- no MIME types, no clock, no I/O.
/// </summary>
public static class ReplyBuilder
{
    private const int MaxReferences = 32;
    private const int MaxQuotedLines = 2_000;

    public static ReplyContext Create(
        ParsedMessage original,
        EmailAddress? self = null,
        bool replyAll = false,
        int maxQuotedChars = 64_000)
    {
        ArgumentNullException.ThrowIfNull(original);

        var rejected = new List<string>();

        var replyTo = RecipientExtractor.ParseAddressList(original.ReplyTo, out var replyToRejected);
        rejected.AddRange(replyToRejected);

        var from = RecipientExtractor.ParseAddressList(original.From, out var fromRejected);
        rejected.AddRange(fromRejected);

        var to = replyTo.Count > 0 ? replyTo : from;
        to = Exclude(to, self, []);

        IReadOnlyList<EmailAddress> cc = [];
        if (replyAll)
        {
            var originalTo = RecipientExtractor.ParseAddressList(original.To, out var toRejected);
            rejected.AddRange(toRejected);

            var originalCc = RecipientExtractor.ParseAddressList(original.Cc, out var ccRejected);
            rejected.AddRange(ccRejected);

            var combined = new List<EmailAddress>(originalTo.Count + originalCc.Count);
            combined.AddRange(originalTo);
            combined.AddRange(originalCc);
            cc = Exclude(combined, self, to);
        }

        return new ReplyContext
        {
            Subject = ReplySubject(original.Subject),
            To = to,
            Cc = cc,
            QuotedBody = Quote(original, maxQuotedChars),
            InReplyTo = original.MessageId,
            References = BuildReferences(original),
            RejectedAddresses = rejected,
        };
    }

    /// <summary>"Re: " prefixed exactly once, whatever the sender's locale prefixed already.</summary>
    public static string ReplySubject(string? subject)
    {
        var core = PlainText.NormalizeHeader(subject, 900) ?? string.Empty;

        for (var i = 0; i < 10; i++)
        {
            var stripped = StripOnePrefix(core);
            if (stripped == core) break;
            core = stripped;
        }

        return core.Length == 0 ? "Re:" : "Re: " + core;
    }

    /// <summary>
    /// References for the reply: the original chain with the message being replied to appended.
    /// Trimmed from the middle when long, keeping the thread root and the most recent ancestors
    /// as RFC 5322 3.6.4 recommends.
    /// </summary>
    public static IReadOnlyList<MessageId> BuildReferences(ParsedMessage original)
    {
        ArgumentNullException.ThrowIfNull(original);

        var chain = new List<MessageId>(original.References.Count + 1);
        foreach (var reference in original.References)
            if (!chain.Contains(reference))
                chain.Add(reference);

        if (original.MessageId is { } id && !chain.Contains(id)) chain.Add(id);
        if (chain.Count <= MaxReferences) return chain;

        var trimmed = new List<MessageId>(MaxReferences) { chain[0] };
        trimmed.AddRange(chain.Skip(chain.Count - (MaxReferences - 1)));
        return trimmed;
    }

    /// <summary>Attribution line plus the original plaintext, each line prefixed with "&gt; ".</summary>
    public static string Quote(ParsedMessage original, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (maxChars <= 0) return string.Empty;

        var sb = new StringBuilder();
        var author = PlainText.NormalizeHeader(original.From, 200);
        var when = original.DateUtc.ToUniversalTime()
            .ToString("ddd, d MMM yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        sb.Append("On ").Append(when);
        if (author is not null) sb.Append(", ").Append(author);
        sb.Append(" wrote:\n\n");

        var body = PlainText.Normalize(original.BodyText, maxChars);
        var lines = 0;

        foreach (var line in body.Split('\n'))
        {
            if (++lines > MaxQuotedLines || sb.Length >= maxChars)
            {
                sb.Append("> [...]\n");
                break;
            }

            sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
        }

        return sb.ToString();
    }

    private static string StripOnePrefix(string subject)
    {
        var s = subject.TrimStart();
        ReadOnlySpan<string> prefixes = ["re:", "re :", "aw:", "sv:", "vs:", "antw:", "res:", "odp:"];

        foreach (var prefix in prefixes)
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return s[prefix.Length..].TrimStart();

        if (s.StartsWith("re[", StringComparison.OrdinalIgnoreCase))
        {
            var close = s.IndexOf(']');
            if (close > 0 && close + 1 < s.Length && s[close + 1] == ':')
                return s[(close + 2)..].TrimStart();
        }

        return subject;
    }

    private static IReadOnlyList<EmailAddress> Exclude(
        IReadOnlyList<EmailAddress> source,
        EmailAddress? self,
        IReadOnlyList<EmailAddress> already)
    {
        var result = new List<EmailAddress>(source.Count);

        foreach (var address in source)
        {
            if (self is { } me && address == me) continue;
            if (result.Contains(address)) continue;
            if (already.Contains(address)) continue;
            result.Add(address);
        }

        return result;
    }
}

/// <summary>The derived parts of a reply. The caller supplies From, body and Message-ID.</summary>
public sealed record ReplyContext
{
    public required string Subject { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public required string QuotedBody { get; init; }
    public MessageId? InReplyTo { get; init; }
    public IReadOnlyList<MessageId> References { get; init; } = [];

    /// <summary>Addresses in the original that failed validation and were dropped rather than mangled.</summary>
    public IReadOnlyList<string> RejectedAddresses { get; init; } = [];
}
