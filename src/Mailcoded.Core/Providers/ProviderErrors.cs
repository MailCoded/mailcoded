using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Mailcoded.Core.Providers;

/// <summary>
/// Translates MailKit failures into <see cref="ProviderException"/> and the RELIABILITY §14.4
/// failure categories that drive reconnect and backoff.
/// </summary>
internal static class ProviderErrors
{
    private const int MaxDetailLength = 200;

    public static ProviderException Imap(Exception ex, string operation) =>
        new(CategorizeImap(ex), $"IMAP {operation} failed: {Describe(ex)}", ex);

    public static ProviderException Smtp(Exception ex, string operation) =>
        new(CategorizeSmtp(ex), $"SMTP {operation} failed: {Describe(ex)}", ex);

    public static FailureCategory CategorizeImap(Exception ex)
    {
        switch (ex)
        {
            case AuthenticationException:
            case ServiceNotAuthenticatedException:
                return FailureCategory.Auth;

            // 30: a captive portal or hijacked DNS surfaces as a handshake failure against a
            // host that was fine yesterday. That is a network fault, never a bad credential.
            case SslHandshakeException:
            case ImapProtocolException:
            case ServiceNotConnectedException:
            case SocketException:
            case IOException:
            case TimeoutException:
                return FailureCategory.Network;

            case FolderNotFoundException:
            case MessageNotFoundException:
                return FailureCategory.NotFound;

            case NotSupportedException:
                return FailureCategory.Unsupported;

            case ImapCommandException command:
                return CategorizeImapCommand(command);

            default:
                return FailureCategory.Protocol;
        }
    }

    public static FailureCategory CategorizeSmtp(Exception ex)
    {
        switch (ex)
        {
            case AuthenticationException:
            case ServiceNotAuthenticatedException:
                return FailureCategory.Auth;

            case SslHandshakeException:
            case SmtpProtocolException:
            case ServiceNotConnectedException:
            case SocketException:
            case IOException:
            case TimeoutException:
                return FailureCategory.Network;

            case SmtpCommandException command:
                return CategorizeSmtpStatus((int)command.StatusCode);

            default:
                return FailureCategory.Protocol;
        }
    }

    /// <summary>28: 421 is a closing channel, a 4xx is worth retrying, and a 5xx is terminal.</summary>
    public static FailureCategory CategorizeSmtpStatus(int status)
    {
        if (status == 421) return FailureCategory.Network;
        if (status == 452) return FailureCategory.Full;
        if (status is >= 400 and < 500) return FailureCategory.Busy;
        if (status >= 500) return FailureCategory.Permanent;
        return FailureCategory.Protocol;
    }

    /// <summary>Strips control characters and truncates. Server text is untrusted input.</summary>
    public static string SanitizeDetail(string? raw, int maxLength = MaxDetailLength)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var limit = raw.Length > maxLength ? maxLength : raw.Length;
        var builder = new StringBuilder(limit);
        for (var i = 0; i < limit; i++)
        {
            var c = raw[i];
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private static FailureCategory CategorizeImapCommand(ImapCommandException ex)
    {
        var text = SanitizeDetail(ex.ResponseText);

        if (Has(text, "OVERQUOTA") || Has(text, "quota")) return FailureCategory.Full;
        if (Has(text, "AUTHENTICATIONFAILED") || Has(text, "AUTHORIZATIONFAILED") || Has(text, "EXPIRED")) return FailureCategory.Auth;
        if (Has(text, "INUSE") || Has(text, "UNAVAILABLE") || Has(text, "LIMIT") || Has(text, "SERVERBUG")) return FailureCategory.Busy;
        if (Has(text, "TRYCREATE") || Has(text, "NONEXISTENT")) return FailureCategory.NotFound;
        if (Has(text, "CANNOT")) return FailureCategory.Unsupported;

        return FailureCategory.Protocol;
    }

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Describe(Exception ex)
    {
        // 3: a credential must never reach an exception message, so an auth failure is described
        // by its shape only and never by the library's own text.
        if (ex is AuthenticationException or ServiceNotAuthenticatedException)
            return "the server rejected the credential";

        return ex switch
        {
            ImapCommandException imap when SanitizeDetail(imap.ResponseText).Length > 0 => SanitizeDetail(imap.ResponseText),
            SmtpCommandException smtp => $"{(int)smtp.StatusCode} {SanitizeDetail(smtp.Message)}",
            _ => SanitizeDetail(ex.Message),
        };
    }
}
