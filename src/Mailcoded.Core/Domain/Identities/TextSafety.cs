namespace Mailcoded.Core.Domain.Identities;

internal static class TextSafety
{
    /// <summary>Invisible format characters that can reorder or hide text without being control characters.</summary>
    public static bool IsInvisibleFormat(char c) =>
        c is (char)0x00AD or (char)0x061C or (char)0xFEFF
        || c is >= (char)0x200B and <= (char)0x200F
        || c is >= (char)0x202A and <= (char)0x202E
        || c is >= (char)0x2060 and <= (char)0x2064
        || c is >= (char)0x2066 and <= (char)0x2069;

    public static bool IsLineBreak(char c) => c is '\n' or '\r' or (char)0x0085 or (char)0x2028 or (char)0x2029;
}
