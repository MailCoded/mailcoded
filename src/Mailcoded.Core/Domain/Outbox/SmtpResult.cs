using System.Text;

namespace Mailcoded.Core.Domain.Outbox;

public enum SendOutcome
{
    Accepted,
    Transient,
    Permanent,
}

/// <summary>An SMTP reply, sanitized for storage. Code 0 means the exchange never produced one.</summary>
public readonly record struct SmtpResult
{
    public const int MaxResponseLength = 512;

    public int Code { get; }
    public string Response { get; }
    public string? EnhancedStatusCode { get; }

    private SmtpResult(int code, string response, string? enhanced)
    {
        Code = code;
        Response = response;
        EnhancedStatusCode = enhanced;
    }

    public static SmtpResult FromResponse(int code, string? response)
    {
        var text = Sanitize(response);
        return new SmtpResult(code, text, TryParseEnhancedStatus(text));
    }

    public static SmtpResult Accepted(string? response) => FromResponse(250, response);

    /// <summary>A failure with no reply at all — socket drop, TLS failure, timeout. Always transient.</summary>
    public static SmtpResult NetworkFailure(string? detail) => new(0, Sanitize(detail), null);

    public SendOutcome Outcome => Code switch
    {
        >= 200 and < 300 => SendOutcome.Accepted,
        >= 500 and < 600 => SendOutcome.Permanent,
        _ => SendOutcome.Transient,
    };

    public bool IsSuccess => Outcome == SendOutcome.Accepted;
    public bool IsPermanent => Outcome == SendOutcome.Permanent;

    /// <summary>421: the service is closing the channel, so retry only after a reconnect.</summary>
    public bool RequiresReconnect => Code == 421;

    /// <summary>450/451: the classic greylisting replies that the retry schedule exists for.</summary>
    public bool IsGreylisted => Code is 450 or 451;

    public override string ToString() => Code == 0 ? Response : $"{Code} {Response}";

    /// <summary>Server text is untrusted: CR/LF and controls are stripped before it can reach a log or the DB.</summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new StringBuilder(Math.Min(raw.Length, MaxResponseLength));
        var gap = false;

        foreach (var c in raw)
        {
            if (sb.Length >= MaxResponseLength) break;
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

    /// <summary>Extracts an RFC 3463 enhanced status code (for example 5.1.1) from a reply line.</summary>
    public static string? TryParseEnhancedStatus(string? response)
    {
        if (string.IsNullOrEmpty(response)) return null;
        var s = response.AsSpan();

        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i])) continue;
            if (i > 0 && (char.IsAsciiDigit(s[i - 1]) || s[i - 1] == '.')) continue;
            if (s[i] is not ('2' or '4' or '5')) continue;
            if (i + 1 >= s.Length || s[i + 1] != '.') continue;

            var j = i + 2;
            var subject = j;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            if (j == subject || j - subject > 3 || j >= s.Length || s[j] != '.') continue;

            j++;
            var detail = j;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            if (j == detail || j - detail > 3) continue;
            if (j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] == '.')) continue;

            return s[i..j].ToString();
        }

        return null;
    }
}
