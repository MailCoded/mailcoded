using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Signals;

namespace Mailcoded.Core.Domain.Correspondents;

/// <summary>Which recipients of an outgoing message are worth remembering. Pure: the caller supplies every fact.</summary>
public static class CorrespondentLearning
{
    /// <summary>A message to more people than this is a broadcast and teaches nothing, so one mailing cannot flood the list.</summary>
    public const int MaxRecipientsPerMessage = 25;

    public static IReadOnlySet<string> OwnKeys(string accountEmail, IReadOnlyList<SenderIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(accountEmail)) keys.Add(accountEmail.Trim().ToLowerInvariant());
        foreach (var identity in identities) keys.Add(NamedAddress.KeyOf(identity.Address));
        return keys;
    }

    /// <summary>True when one of the From addresses is the Account's own or a configured identity; a message someone else wrote is not outgoing mail.</summary>
    public static bool IsAuthoredBy(IReadOnlyList<NamedAddress> from, IReadOnlySet<string> ownKeys)
    {
        foreach (var author in from)
            if (ownKeys.Contains(author.Key)) return true;

        return false;
    }

    /// <summary>
    /// To and Cc only: a Bcc list is hidden on purpose and a mass Bcc would flood the list. Own addresses and
    /// robot addresses are dropped, a repeated address keeps its first name, and a broadcast yields nothing.
    /// </summary>
    public static IReadOnlyList<NamedAddress> Select(
        IReadOnlyList<NamedAddress> to,
        IReadOnlyList<NamedAddress> cc,
        IReadOnlySet<string> ownKeys)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(cc);
        ArgumentNullException.ThrowIfNull(ownKeys);

        var distinct = new List<NamedAddress>(to.Count + cc.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var list in new[] { to, cc })
        {
            foreach (var entry in list)
            {
                if (seen.TryGetValue(entry.Key, out var at))
                {
                    if (distinct[at].DisplayName is null && entry.DisplayName is not null)
                        distinct[at] = distinct[at] with { DisplayName = entry.DisplayName };
                    continue;
                }

                seen[entry.Key] = distinct.Count;
                distinct.Add(entry);
            }
        }

        if (distinct.Count > MaxRecipientsPerMessage) return [];

        var kept = new List<NamedAddress>(distinct.Count);
        foreach (var entry in distinct)
        {
            if (ownKeys.Contains(entry.Key)) continue;
            if (MessageSignals.IsRobotLocalPart(entry.Address.LocalPart)) continue;
            kept.Add(entry);
        }

        return kept;
    }
}
