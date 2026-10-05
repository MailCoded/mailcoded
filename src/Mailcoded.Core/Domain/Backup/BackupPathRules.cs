namespace Mailcoded.Core.Domain.Backup;

public static class BackupPathRules
{
    public const int MaxPathChars = 4096;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private const string WindowsForbiddenChars = "<>\"|?*";

    public static string? Check(string? path, bool windows)
    {
        if (string.IsNullOrWhiteSpace(path)) return "A backup destination is required.";
        if (path.Length > MaxPathChars) return "The destination path is too long.";

        foreach (var c in path)
        {
            if (char.IsControl(c)) return "The destination path contains a control character.";
        }

        return windows ? CheckWindows(path) : null;
    }

    public static bool IsSameOrInside(string candidate, string root, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(root);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var left = Normalize(candidate);
        var right = Normalize(root);

        if (right.Length == 0) return true;
        if (left.Equals(right, comparison)) return true;
        if (!left.StartsWith(right, comparison)) return false;

        return right[^1] == '/' || left[right.Length] == '/';
    }

    private static string Normalize(string path)
    {
        var text = path.Replace('\\', '/');
        var end = text.Length;
        while (end > 1 && text[end - 1] == '/') end--;
        return text[..end];
    }

    private static string? CheckWindows(string path)
    {
        var text = path.Replace('/', '\\');

        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            return "Device and extended-length paths are not accepted as a backup destination.";

        var start = 0;
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
            start = 2;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ':') return "A colon in a path component names an alternate data stream; it is not accepted.";
            if (WindowsForbiddenChars.Contains(c, StringComparison.Ordinal)) return "The destination path contains a character Windows does not allow.";
        }

        foreach (var component in text[start..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..") continue;
            if (component[^1] is ' ' or '.') return "A path component ends in a space or a dot, which Windows silently drops.";

            var dot = component.IndexOf('.', StringComparison.Ordinal);
            var stem = dot < 0 ? component : component[..dot];
            if (Array.Exists(ReservedDeviceNames, name => string.Equals(name, stem.TrimEnd(), StringComparison.OrdinalIgnoreCase)))
                return "A path component is a reserved Windows device name.";
        }

        return null;
    }
}
