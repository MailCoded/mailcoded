using Mailcoded.Core.Domain.Primitives;
using MimeKit;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// <see cref="DraftSpec"/> -> RFC822 bytes. The Message-ID always comes from the spec, which the
/// outbox assigned at creation (RELIABILITY 14.4) -- minting one here would break send idempotency.
/// </summary>
public static class MessageBuilder
{
    public static byte[] Build(DraftSpec spec, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        WriteTo(spec, buffer, ct);
        return buffer.ToArray();
    }

    public static void WriteTo(DraftSpec spec, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(destination);

        using var message = Compose(spec);
        var format = FormatOptions.Default.Clone();
        format.International = RequiresSmtpUtf8(spec);
        message.WriteTo(format, destination, ct);
    }

    /// <summary>True when any address carries a non-ASCII octet, so the sender must pre-check SMTPUTF8.</summary>
    public static bool RequiresSmtpUtf8(DraftSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.From.RequiresSmtpUtf8()) return true;
        foreach (var a in spec.To) if (a.RequiresSmtpUtf8()) return true;
        foreach (var a in spec.Cc) if (a.RequiresSmtpUtf8()) return true;
        foreach (var a in spec.Bcc) if (a.RequiresSmtpUtf8()) return true;
        return false;
    }

    private static MimeMessage Compose(DraftSpec spec)
    {
        RejectInjection(spec.Subject, "Subject");
        RejectInjection(spec.FromDisplayName, "FromDisplayName");

        if (spec.DateUtc == default)
            throw new ArgumentException("DraftSpec.DateUtc must be set by the caller's IClock.", nameof(spec));

        if (string.IsNullOrEmpty(spec.MessageId.Value))
            throw new ArgumentException("DraftSpec.MessageId must be pre-assigned at outbox creation.", nameof(spec));

        if (spec.To.Count + spec.Cc.Count + spec.Bcc.Count == 0)
            throw new ArgumentException("DraftSpec has no recipients.", nameof(spec));

        MimeCharsets.EnsureRegistered();

        var message = new MimeMessage();
        try
        {
            message.From.Add(ToMailbox(spec.From, spec.FromDisplayName, "From"));
            AddAll(message.To, spec.To, "To");
            AddAll(message.Cc, spec.Cc, "Cc");
            AddAll(message.Bcc, spec.Bcc, "Bcc");

            message.Subject = spec.Subject;
            message.Date = spec.DateUtc;

            try
            {
                message.MessageId = spec.MessageId.Value;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new ArgumentException("DraftSpec.MessageId is not a valid RFC 5322 msg-id.", nameof(spec), ex);
            }

            if (spec.InReplyTo is { } inReplyTo && !string.IsNullOrEmpty(inReplyTo.Value))
            {
                try
                {
                    message.InReplyTo = inReplyTo.Value;
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    throw new ArgumentException("DraftSpec.InReplyTo is not a valid RFC 5322 msg-id.", nameof(spec), ex);
                }
            }

            var added = 0;
            foreach (var reference in spec.References)
            {
                if (added++ >= 64) break;
                if (string.IsNullOrEmpty(reference.Value)) continue;

                try
                {
                    message.References.Add(reference.Value);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    throw new ArgumentException("DraftSpec.References contains an invalid msg-id.", nameof(spec), ex);
                }
            }

            message.Body = new TextPart("plain") { Text = spec.BodyText };
            message.Prepare(EncodingConstraint.SevenBit);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static void AddAll(InternetAddressList target, IReadOnlyList<EmailAddress> source, string field)
    {
        foreach (var address in source) target.Add(ToMailbox(address, null, field));
    }

    private static MailboxAddress ToMailbox(EmailAddress address, string? displayName, string field)
    {
        // EmailAddress already excludes CR/LF; re-validating keeps the injection gate honest if that changes.
        if (!EmailAddress.TryParse(address.Value, out var validated))
            throw new ArgumentException($"{field} contains an address that is not RFC-valid.", "spec");

        return new MailboxAddress(displayName ?? string.Empty, validated.Value);
    }

    private static void RejectInjection(string? value, string field)
    {
        if (value is null) return;

        foreach (var c in value)
        {
            if (c is '\r' or '\n' or '\0' || (char.IsControl(c) && c != '\t'))
                throw new ArgumentException($"{field} contains a control character; header injection rejected.", "spec");
        }
    }
}
