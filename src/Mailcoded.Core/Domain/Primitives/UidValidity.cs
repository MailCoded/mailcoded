using System.Globalization;

namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// An IMAP UIDVALIDITY token. Equality-only semantics by design: a UIDVALIDITY is an opaque
/// epoch marker, so comparing two of them for ordering is always a bug.
/// </summary>
public readonly record struct UidValidity
{
    public uint Value { get; }
    public UidValidity(uint value) => Value = value;

    public static readonly UidValidity Unknown = new(0);
    public bool IsUnknown => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
