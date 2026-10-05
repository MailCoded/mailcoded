using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Identities;

/// <summary>Which addresses an Account may send as. Pure rules; the account address is always one of them.</summary>
public static class SenderIdentities
{
    public const int MaxIdentities = 32;

    public static bool Same(EmailAddress a, EmailAddress b) =>
        string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Null <paramref name="requested"/> means the account address. The result carries the configured spelling.</summary>
    public static bool TryResolve(
        EmailAddress account,
        string? accountDisplayName,
        IReadOnlyList<SenderIdentity> identities,
        EmailAddress? requested,
        out ResolvedSender sender)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var wanted = requested ?? account;

        foreach (var identity in identities)
        {
            if (!Same(identity.Address, wanted)) continue;
            sender = new ResolvedSender(identity.Address, identity.DisplayName ?? accountDisplayName, identity.ReplyTo);
            return true;
        }

        if (Same(account, wanted))
        {
            sender = new ResolvedSender(account, accountDisplayName, null);
            return true;
        }

        sender = default;
        return false;
    }

    /// <summary>The first of the original's recipients, To before Cc, that this Account owns; else the account address.</summary>
    public static EmailAddress PickForReply(
        EmailAddress account,
        IReadOnlyList<SenderIdentity> identities,
        IReadOnlyList<EmailAddress> originalRecipients)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(originalRecipients);

        foreach (var recipient in originalRecipients)
        {
            if (Same(recipient, account)) return account;

            foreach (var identity in identities)
                if (Same(recipient, identity.Address)) return identity.Address;
        }

        return account;
    }

    public static IReadOnlyList<EmailAddress> OwnAddresses(EmailAddress account, IReadOnlyList<SenderIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var own = new List<EmailAddress>(identities.Count + 1) { account };
        foreach (var identity in identities)
            if (!own.Exists(a => Same(a, identity.Address))) own.Add(identity.Address);

        return own;
    }

    /// <summary>Adds the identity, or replaces the one with the same address. Throws when the list would pass the cap.</summary>
    public static IReadOnlyList<SenderIdentity> With(IReadOnlyList<SenderIdentity> identities, SenderIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(identity);

        var result = new List<SenderIdentity>(identities.Count + 1);
        var replaced = false;

        foreach (var existing in identities)
        {
            if (Same(existing.Address, identity.Address))
            {
                result.Add(identity);
                replaced = true;
            }
            else
            {
                result.Add(existing);
            }
        }

        if (!replaced)
        {
            if (result.Count >= MaxIdentities)
                throw new ArgumentException($"An account can have at most {MaxIdentities} identities.");

            result.Add(identity);
        }

        return result;
    }

    public static IReadOnlyList<SenderIdentity> Without(
        IReadOnlyList<SenderIdentity> identities,
        EmailAddress address,
        out bool removed)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var result = new List<SenderIdentity>(identities.Count);
        foreach (var existing in identities)
            if (!Same(existing.Address, address)) result.Add(existing);

        removed = result.Count != identities.Count;
        return result;
    }
}
