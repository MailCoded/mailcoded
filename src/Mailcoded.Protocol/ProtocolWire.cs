using System.Globalization;

namespace Mailcoded.Protocol;

/// <summary>Wire vocabulary for server IMAP flags. Tags are a separate vocabulary.</summary>
/// <remarks>Names only: the MessageFlags mapping lives in the daemon, so this assembly
/// stays free of engine types and a client author can take it alone.</remarks>
public static class FlagNames
{
    public const string Unread = "unread";
    public const string Flagged = "flagged";
    public const string Answered = "answered";
    public const string Draft = "draft";
    public const string Deleted = "deleted";
    public const string Recent = "recent";

    public static IReadOnlyList<string> All { get; } =
        [Unread, Flagged, Answered, Draft, Deleted, Recent];

    public static bool IsKnown(string? name) => name switch
    {
        Unread or Flagged or Answered or Draft or Deleted or Recent => true,
        _ => false,
    };
}

/// <summary>ISO 8601, always UTC, always Z-suffixed, so no client infers an offset.</summary>
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

/// <summary>64-bit unsigned values travel as decimal strings: JSON numbers lose precision above 2^53.</summary>
public static class ModSeqWire
{
    public static string ToWire(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out ulong modSeq) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out modSeq);
}
