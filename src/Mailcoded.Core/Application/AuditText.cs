using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mailcoded.Core.Application;

/// <summary>Builders for audit detail strings. Email content never reaches one; only digests do.</summary>
public static class AuditText
{
    public const int MaxDetailLength = 512;
    public const int DigestHexLength = 16;

    /// <summary>Strips control characters and caps length so untrusted text cannot shape a log line.</summary>
    public static string? Sanitize(string? value, int maxLength = MaxDetailLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (maxLength <= 0) return null;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var gap = false;

        foreach (var c in value)
        {
            if (builder.Length >= maxLength) break;

            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (builder.Length > 0) gap = true;
                continue;
            }

            if (gap)
            {
                builder.Append(' ');
                gap = false;
                if (builder.Length >= maxLength) break;
            }

            builder.Append(c);
        }

        var result = builder.ToString();
        return result.Length == 0 ? null : result;
    }

    /// <summary>A short hash prefix standing in for arguments the audit row must never carry.</summary>
    public static string Digest(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(content, hash);
        return Convert.ToHexStringLower(hash)[..DigestHexLength];
    }

    public static string Digest(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new string('0', DigestHexLength);
        return Digest(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Composes an audit detail as <c>key=value</c> pairs, sanitizing every value.</summary>
    public static string Fields(params (string Key, string? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var builder = new StringBuilder(128);
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrEmpty(key)) continue;
            var clean = Sanitize(value, 128);
            if (clean is null) continue;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(key).Append('=').Append(clean);
            if (builder.Length >= MaxDetailLength) break;
        }

        var result = builder.ToString();
        return result.Length > MaxDetailLength ? result[..MaxDetailLength] : result;
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "true" : "false";
}
