using System.Globalization;

namespace Mailcoded.Core.Store;

/// <summary>
/// Opaque page cursors. Keyset form carries the last row's (date_utc, id); offset form is only
/// ever used by relevance-ordered search and is capped, never used for deep paging.
/// </summary>
internal static class Cursors
{
    public static string EncodeKeyset(long dateUtc, long id) =>
        string.Concat("k", dateUtc.ToString(CultureInfo.InvariantCulture), ".", id.ToString(CultureInfo.InvariantCulture));

    public static bool TryDecodeKeyset(string? cursor, out long dateUtc, out long id)
    {
        dateUtc = 0;
        id = 0;
        if (string.IsNullOrEmpty(cursor) || cursor[0] != 'k') return false;

        var body = cursor.AsSpan(1);
        var dot = body.LastIndexOf('.');
        if (dot <= 0 || dot == body.Length - 1) return false;

        return long.TryParse(body[..dot], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out dateUtc)
            && long.TryParse(body[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static string EncodeOffset(int offset) =>
        string.Concat("o", offset.ToString(CultureInfo.InvariantCulture));

    public static bool TryDecodeOffset(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrEmpty(cursor) || cursor[0] != 'o') return false;
        return int.TryParse(cursor.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out offset);
    }
}
