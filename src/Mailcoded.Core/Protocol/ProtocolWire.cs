using System.Globalization;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Protocol;

/// <summary>Wire vocabulary for server IMAP flags. Tags are a separate vocabulary and never appear here.</summary>
public static class FlagNames
{
    public const string Unread = "unread";
    public const string Flagged = "flagged";
    public const string Answered = "answered";
    public const string Draft = "draft";
    public const string Deleted = "deleted";
    public const string Recent = "recent";

    public static IReadOnlyList<string> From(MessageFlags flags)
    {
        if (flags == MessageFlags.None) return [];

        var names = new List<string>(6);
        if (flags.HasFlag(MessageFlags.Unread)) names.Add(Unread);
        if (flags.HasFlag(MessageFlags.Flagged)) names.Add(Flagged);
        if (flags.HasFlag(MessageFlags.Answered)) names.Add(Answered);
        if (flags.HasFlag(MessageFlags.Draft)) names.Add(Draft);
        if (flags.HasFlag(MessageFlags.Deleted)) names.Add(Deleted);
        if (flags.HasFlag(MessageFlags.Recent)) names.Add(Recent);
        return names;
    }

    /// <summary>Unknown names are ignored rather than rejected; the flag set is server-owned, not client-owned.</summary>
    public static MessageFlags Parse(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return MessageFlags.None;

        var flags = MessageFlags.None;
        for (var i = 0; i < names.Count; i++)
        {
            flags |= names[i] switch
            {
                Unread => MessageFlags.Unread,
                Flagged => MessageFlags.Flagged,
                Answered => MessageFlags.Answered,
                Draft => MessageFlags.Draft,
                Deleted => MessageFlags.Deleted,
                Recent => MessageFlags.Recent,
                _ => MessageFlags.None,
            };
        }

        return flags;
    }
}

/// <summary>
/// The single date format on the wire: ISO 8601, always UTC, always <c>Z</c>-suffixed, so no client
/// has to infer an offset.
/// </summary>
public static class IsoTime
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string ToWire(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    public static string? ToWireOrNull(DateTimeOffset? value) =>
        value is { } v ? ToWire(v) : null;

    public static bool TryParse(string? value, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;

        utc = parsed.ToUniversalTime();
        return true;
    }
}

/// <summary>MODSEQ and other 64-bit unsigned values travel as decimal strings: JSON numbers lose precision above 2^53.</summary>
public static class ModSeqWire
{
    public static string ToWire(ModSeq value) => value.Value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out ModSeq modSeq)
    {
        modSeq = ModSeq.Zero;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return false;
        modSeq = new ModSeq(v);
        return true;
    }
}
