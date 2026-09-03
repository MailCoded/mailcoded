using System.Globalization;
using System.Text;

namespace Mailcoded.Tui.Render;

/// <summary>UAX #11 East Asian Width, reduced to the ranges that are 2 columns wide.</summary>
internal static class DisplayWidth
{
    private static readonly int[] WideStarts =
    [
        0x1100, 0x2E80, 0x303F, 0x3099, 0x3250, 0x4DC0, 0x4E00, 0xA000, 0xA4D0, 0xA960,
        0xAC00, 0xF900, 0xFE10, 0xFE30, 0xFF00, 0xFFE0,
        0x16FE0, 0x17000, 0x18800, 0x1AFF0, 0x1B000, 0x1B100, 0x1B170, 0x1F004, 0x1F0CF,
        0x1F18E, 0x1F191, 0x1F200, 0x1F300, 0x1F3E0, 0x1F440, 0x1F4FF, 0x1F5FB, 0x1F680,
        0x1F900, 0x1FA70, 0x20000, 0x30000,
    ];

    private static readonly int[] WideEnds =
    [
        0x115F, 0x303E, 0x303E, 0x30FF, 0x4DBF, 0x4DBF, 0x9FFF, 0xA48F, 0xA4CF, 0xA97F,
        0xD7A3, 0xFAFF, 0xFE19, 0xFE6F, 0xFF60, 0xFFE6,
        0x16FE4, 0x18AFF, 0x18CD5, 0x1AFFF, 0x1B0FF, 0x1B12F, 0x1B2FF, 0x1F004, 0x1F0CF,
        0x1F18E, 0x1F19A, 0x1F2FF, 0x1F320, 0x1F3F0, 0x1F4FC, 0x1F53D, 0x1F64F, 0x1F6FF,
        0x1F9FF, 0x1FAFF, 0x2FFFD, 0x3FFFD,
    ];

    public static int OfRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);

        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark)
        {
            return 0;
        }

        // Hangul Jamo medial and final are conjoining: they compose into the preceding syllable.
        if (rune.Value is >= 0x1160 and <= 0x11FF) return 0;

        return IsWide(rune.Value) ? 2 : 1;
    }

    /// <summary>Width of a grapheme cluster: the base advance, with combining marks adding none.</summary>
    public static int OfCluster(string cluster)
    {
        var width = 0;
        foreach (var rune in cluster.EnumerateRunes())
        {
            var runeWidth = OfRune(rune);
            if (runeWidth > width) width = runeWidth;
        }

        return width == 0 ? 1 : width;
    }

    private static bool IsWide(int value)
    {
        var low = 0;
        var high = WideStarts.Length - 1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (value < WideStarts[mid]) high = mid - 1;
            else if (value > WideEnds[mid]) low = mid + 1;
            else return true;
        }

        return false;
    }
}
