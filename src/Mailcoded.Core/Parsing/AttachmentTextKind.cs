namespace Mailcoded.Core.Parsing;

public enum AttachmentTextKind
{
    None,
    Text,
    Html,
    Docx,
}

public static class AttachmentTextClassifier
{
    public const string DocxMimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static AttachmentTextKind Classify(string? mimeType, string? fileName)
    {
        var mime = Normalize(mimeType);
        var extension = ExtensionOf(fileName);

        switch (mime)
        {
            case DocxMimeType:
                return AttachmentTextKind.Docx;

            case "text/plain" or "text/csv" or "text/tab-separated-values" or "text/markdown"
                or "text/x-markdown" or "text/calendar" or "application/json":
                return AttachmentTextKind.Text;

            case "text/html":
                return AttachmentTextKind.Html;
        }

        if (!IsGeneric(mime)) return AttachmentTextKind.None;

        return extension switch
        {
            ".docx" => AttachmentTextKind.Docx,
            ".txt" or ".csv" or ".tsv" or ".md" or ".markdown" or ".log" or ".json" or ".ics" => AttachmentTextKind.Text,
            _ => AttachmentTextKind.None,
        };
    }

    private static bool IsGeneric(string mime) =>
        mime is "" or "application/octet-stream" or "binary/octet-stream" or "application/x-download"
            or "application/force-download" or "application/zip" or "application/x-zip-compressed";

    private static string Normalize(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return string.Empty;

        var value = mimeType.Trim();
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0) value = value[..semicolon];
        return value.Trim().ToLowerInvariant();
    }

    private static string ExtensionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return string.Empty;

        var dot = fileName.LastIndexOf('.');
        return dot < 0 || fileName.Length - dot > 12 ? string.Empty : fileName[dot..].ToLowerInvariant();
    }
}
