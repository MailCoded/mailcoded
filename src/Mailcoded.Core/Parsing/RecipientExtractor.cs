using System.Diagnostics.CodeAnalysis;
using Mailcoded.Core.Domain.Primitives;
using MimeKit;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// Turns header text into validated <see cref="EmailAddress"/> values. Anything that fails
/// validation is reported, never silently repaired: a mangled recipient is a mis-delivery.
/// </summary>
public static class RecipientExtractor
{
    private const int MaxAddresses = 256;
    private const int MaxRejectedReported = 32;

    /// <summary>Parses one address-list header. Never throws.</summary>
    public static IReadOnlyList<EmailAddress> ParseAddressList(string? headerValue, out IReadOnlyList<string> rejected)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            rejected = [];
            return [];
        }

        MimeCharsets.EnsureRegistered();

        InternetAddressList? list = null;
        try
        {
            if (!InternetAddressList.TryParse(headerValue, out var parsed)) list = null;
            else list = parsed;
        }
        catch (Exception)
        {
            list = null;
        }

        if (list is null)
        {
            rejected = [Describe(headerValue)];
            return [];
        }

        var accepted = new List<EmailAddress>();
        var failures = new List<string>();
        Collect(list, accepted, failures, 0);

        rejected = failures;
        return accepted;
    }

    /// <summary>
    /// Recipients of an already-parsed message, for reply and audit paths.
    /// Returns false when the From address is missing or unusable.
    /// </summary>
    public static bool TryExtract(
        ParsedMessage message,
        [NotNullWhen(true)] out ParsedRecipients? recipients,
        out IReadOnlyList<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(message);

        var failures = new List<string>();

        var from = ParseAddressList(message.From, out var fromRejected);
        failures.AddRange(fromRejected);

        var to = ParseAddressList(message.To, out var toRejected);
        failures.AddRange(toRejected);

        var cc = ParseAddressList(message.Cc, out var ccRejected);
        failures.AddRange(ccRejected);

        rejected = failures;

        if (from.Count == 0)
        {
            recipients = null;
            return false;
        }

        recipients = new ParsedRecipients
        {
            From = from[0],
            To = to,
            Cc = cc,
        };

        return true;
    }

    /// <summary>The envelope a draft will be submitted with.</summary>
    public static ParsedRecipients FromDraft(DraftSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return new ParsedRecipients
        {
            From = spec.From,
            To = spec.To,
            Cc = spec.Cc,
            Bcc = spec.Bcc,
        };
    }

    private static void Collect(InternetAddressList list, List<EmailAddress> accepted, List<string> rejected, int depth)
    {
        if (depth > 4) return;

        foreach (var entry in list)
        {
            if (accepted.Count >= MaxAddresses) return;

            switch (entry)
            {
                case GroupAddress group:
                    Collect(group.Members, accepted, rejected, depth + 1);
                    break;

                case MailboxAddress mailbox:
                    if (EmailAddress.TryParse(mailbox.Address, out var address))
                    {
                        if (!accepted.Contains(address)) accepted.Add(address);
                    }
                    else if (rejected.Count < MaxRejectedReported)
                    {
                        rejected.Add(Describe(mailbox.Address));
                    }

                    break;
            }
        }
    }

    private static string Describe(string? value) =>
        PlainText.NormalizeHeader(value, 128) ?? "(empty)";
}
