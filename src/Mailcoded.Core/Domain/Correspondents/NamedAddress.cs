using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Correspondents;

/// <summary>An address as a header carried it, with the display name that went with it, if any.</summary>
public readonly record struct NamedAddress(EmailAddress Address, string? DisplayName)
{
    /// <summary>The identity of a correspondent: the whole address, lower-cased.</summary>
    public string Key => KeyOf(Address);

    public static string KeyOf(EmailAddress address) => address.Value.ToLowerInvariant();
}
