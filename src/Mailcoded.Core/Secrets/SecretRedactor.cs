using System.Text;

namespace Mailcoded.Core.Secrets;

/// <summary>
/// Scrubs strings that may carry a credential before they reach a log line, an exception message,
/// or an RPC response. Every store in this folder routes user-facing text through it.
/// </summary>
public static class SecretRedactor
{
    public const string Mask = "[redacted]";

    private static readonly string[] SensitiveKeyParts =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key",
        "authorization", "auth", "credential", "passphrase", "sessionkey", "refresh"
    ];

    /// <summary>Replaces a value that is known to be a credential.</summary>
    public static string Redact(string? value) => Mask;

    /// <summary>True when a configuration/header/env key is likely to carry a credential.</summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var part in SensitiveKeyParts)
        {
            if (key.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Returns <paramref name="value"/> unchanged unless the key looks credential-bearing.</summary>
    public static string RedactValueFor(string? key, string? value) =>
        IsSensitiveKey(key) ? Mask : value ?? string.Empty;

    /// <summary>Removes every occurrence of the given secrets from arbitrary text.</summary>
    public static string RedactSubstrings(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = text;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 4) continue;
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Strips userinfo and credential-bearing query values from a URI-shaped string.</summary>
    public static string RedactUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return string.Empty;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return Mask;

        var sb = new StringBuilder();
        sb.Append(parsed.Scheme).Append("://");
        if (!string.IsNullOrEmpty(parsed.UserInfo)) sb.Append(Mask).Append('@');
        sb.Append(parsed.Host);
        if (!parsed.IsDefaultPort) sb.Append(':').Append(parsed.Port);
        sb.Append(parsed.AbsolutePath);
        if (parsed.Query.Length > 1) sb.Append('?').Append(Mask);
        return sb.ToString();
    }

    /// <summary>
    /// Makes a secret reference safe to print. References are opaque handles, never credentials,
    /// but untrusted input must not smuggle control characters into a log line.
    /// </summary>
    public static string SafeRef(string? secretRef)
    {
        if (string.IsNullOrEmpty(secretRef)) return "<none>";
        var limit = Math.Min(secretRef.Length, 128);
        var sb = new StringBuilder(limit + 1);
        for (var i = 0; i < limit; i++)
        {
            var c = secretRef[i];
            sb.Append(char.IsControl(c) || c >= (char)0x7f && c <= (char)0x9f ? '?' : c);
        }
        if (secretRef.Length > limit) sb.Append("...");
        return sb.ToString();
    }
}
