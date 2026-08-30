using System.Globalization;

namespace Mailcoded.Core.Domain.Primitives;

/// <summary>An IMAP message UID. Always &gt; 0; ordered within one <see cref="UidValidity"/> epoch.</summary>
public readonly record struct Uid : IComparable<Uid>
{
    public uint Value { get; }

    public Uid(uint value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value), "IMAP UID must be greater than zero.");
        Value = value;
    }

    public static bool TryParse(string? s, out Uid uid)
    {
        uid = default;
        if (!uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v == 0) return false;
        uid = new Uid(v);
        return true;
    }

    public static bool TryCreate(long value, out Uid uid)
    {
        uid = default;
        if (value <= 0 || value > uint.MaxValue) return false;
        uid = new Uid((uint)value);
        return true;
    }

    public int CompareTo(Uid other) => Value.CompareTo(other.Value);
    public static bool operator <(Uid a, Uid b) => a.Value < b.Value;
    public static bool operator >(Uid a, Uid b) => a.Value > b.Value;
    public static bool operator <=(Uid a, Uid b) => a.Value <= b.Value;
    public static bool operator >=(Uid a, Uid b) => a.Value >= b.Value;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
