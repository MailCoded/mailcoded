using System.Text;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// Attachment filenames arrive from the sender and are never trusted.
/// <see cref="ParsedAttachment.FileName"/> keeps the value verbatim for display; anything that
/// reaches a filesystem must go through <see cref="ToSaveAsFileName"/> first.
/// </summary>
public static class AttachmentNaming
{
    private const int MaxLength = 120;

    private static readonly string[] ReservedStems =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// A single path segment safe to combine with a caller-chosen directory on any OS.
    /// Never absolute, never contains a separator, never traverses, never a Windows device name.
    /// </summary>
    public static string ToSaveAsFileName(string? proposed, int index, string? mimeType = null)
    {
        var candidate = StripDirectories(proposed);
        var sb = new StringBuilder(Math.Min(candidate.Length, MaxLength) + 8);

        foreach (var c in candidate)
        {
            if (sb.Length >= MaxLength) break;
            if (char.IsControl(c) || PlainText.IsInvisible(c)) continue;
            sb.Append(IsUnsafe(c) ? '_' : c);
        }

        var name = sb.ToString().Trim();
        while (name.Length > 0 && (name[0] == '.' || name[0] == '-' || name[0] == ' ')) name = name[1..].TrimStart();
        name = name.TrimEnd('.', ' ');

        if (name.Length == 0) name = Fallback(index, mimeType);
        if (IsReserved(name)) name = "_" + name;

        return name;
    }

    private static string StripDirectories(string? proposed)
    {
        if (string.IsNullOrWhiteSpace(proposed)) return string.Empty;

        var s = proposed;
        var cut = s.LastIndexOfAny(['/', '\\', ':']);
        if (cut >= 0) s = s[(cut + 1)..];

        return s.Length > 512 ? s[..512] : s;
    }

    private static bool IsUnsafe(char c) =>
        c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '\0';

    private static bool IsReserved(string name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];

        foreach (var reserved in ReservedStems)
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static string Fallback(int index, string? mimeType) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"attachment-{index}{ExtensionFor(mimeType)}");

    private static string ExtensionFor(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return ".bin";

        return mimeType.Trim().ToLowerInvariant() switch
        {
            "text/plain" => ".txt",
            "text/html" => ".html",
            "text/calendar" => ".ics",
            "text/csv" => ".csv",
            "message/rfc822" => ".eml",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "application/json" => ".json",
            "application/rtf" => ".rtf",
            "application/msword" => ".doc",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/ms-tnef" or "application/vnd.ms-tnef" => ".dat",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "audio/mpeg" => ".mp3",
            "video/mp4" => ".mp4",
            _ => ".bin",
        };
    }
}
