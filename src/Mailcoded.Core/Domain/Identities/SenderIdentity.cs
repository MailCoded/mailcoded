using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Identities;

/// <summary>An extra From address an Account may send as, with the name and Reply-To that go with it.</summary>
public sealed record SenderIdentity
{
    public const int MaxDisplayNameLength = 100;

    public required EmailAddress Address { get; init; }

    public string? DisplayName { get; init; }

    public EmailAddress? ReplyTo { get; init; }

    public static bool TryCreate(
        string? address,
        string? displayName,
        string? replyTo,
        out SenderIdentity identity,
        out string error)
    {
        identity = null!;

        if (!EmailAddress.TryParse(address, out var parsed))
        {
            error = "The identity address is not a valid email address.";
            return false;
        }

        string? name = null;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            name = displayName.Trim();
            if (name.Length > MaxDisplayNameLength)
            {
                error = $"The display name is longer than {MaxDisplayNameLength} characters.";
                return false;
            }

            foreach (var c in name)
            {
                if (char.IsControl(c) || TextSafety.IsInvisibleFormat(c) || TextSafety.IsLineBreak(c))
                {
                    error = "The display name must not contain control characters or line breaks.";
                    return false;
                }
            }
        }

        EmailAddress? reply = null;
        if (!string.IsNullOrWhiteSpace(replyTo))
        {
            if (!EmailAddress.TryParse(replyTo, out var parsedReply))
            {
                error = "The Reply-To address is not a valid email address.";
                return false;
            }

            reply = parsedReply;
        }

        identity = new SenderIdentity { Address = parsed, DisplayName = name, ReplyTo = reply };
        error = string.Empty;
        return true;
    }
}

/// <summary>What a draft is sent as once its From has been checked.</summary>
public readonly record struct ResolvedSender(EmailAddress Address, string? DisplayName, EmailAddress? ReplyTo);
