namespace Mailcoded.Core.Domain.Signals;

/// <summary>The few Content-Type facts message signals need, free of any MIME library type.</summary>
public readonly record struct ContentShape(
    string MediaType,
    string MediaSubtype,
    string? Protocol = null,
    string? SmimeType = null,
    string? ReportType = null)
{
    public bool Is(string mediaType, string mediaSubtype) =>
        MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase)
        && MediaSubtype.Equals(mediaSubtype, StringComparison.OrdinalIgnoreCase);
}
