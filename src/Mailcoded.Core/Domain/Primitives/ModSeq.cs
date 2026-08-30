using System.Globalization;

namespace Mailcoded.Core.Domain.Primitives;

/// <summary>An IMAP CONDSTORE MODSEQ. Zero means "unknown / server did not report one".</summary>
public readonly record struct ModSeq : IComparable<ModSeq>
{
    public ulong Value { get; }
    public ModSeq(ulong value) => Value = value;

    public static readonly ModSeq Zero = new(0);
    public bool IsUnknown => Value == 0;

    public int CompareTo(ModSeq other) => Value.CompareTo(other.Value);
    public static bool operator <(ModSeq a, ModSeq b) => a.Value < b.Value;
    public static bool operator >(ModSeq a, ModSeq b) => a.Value > b.Value;
    public static bool operator <=(ModSeq a, ModSeq b) => a.Value <= b.Value;
    public static bool operator >=(ModSeq a, ModSeq b) => a.Value >= b.Value;
    public static ModSeq Max(ModSeq a, ModSeq b) => a.Value >= b.Value ? a : b;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
