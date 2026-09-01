namespace Mailcoded.Core.Parsing;

/// <summary>Bounds for parsing untrusted mail, so a hostile message cannot turn into unbounded memory,
/// CPU or FTS content. Exceeding one is a <see cref="ParsedMessage.ParseWarnings"/> entry, except for
/// the raw-shape bounds, which refuse the message outright.</summary>
public sealed record MessageParserOptions
{
    public static MessageParserOptions Default { get; } = new();

    /// <summary>Total plaintext kept for FTS across every body part.</summary>
    public int MaxBodyTextChars { get; init; } = 512_000;

    /// <summary>Raw HTML alternative handed to the client.</summary>
    public int MaxHtmlChars { get; init; } = 1_000_000;

    /// <summary>Bytes decoded from any single text part before truncation.</summary>
    public int MaxPartBytes { get; init; } = 1_048_576;

    /// <summary>Characters kept from any single header value.</summary>
    public int MaxHeaderChars { get; init; } = 8_000;

    /// <summary>Depth of multipart/message-part nesting walked before the walk stops descending.</summary>
    public int MaxDepth { get; init; } = 24;

    /// <summary>Entities visited in the whole tree.</summary>
    public int MaxParts { get; init; } = 512;

    /// <summary>Boundary delimiter lines the raw bytes may carry. Checked before MimeKit builds the
    /// tree, because the tree is the allocation a part-count bomb is buying.</summary>
    public int MaxRawBoundaryLines { get; init; } = 4_096;

    /// <summary>Backstop for lines that open a part but match no boundary this scan saw declared.</summary>
    public int MaxRawDelimiterCandidates { get; init; } = 16_384;

    public int MaxAttachments { get; init; } = 512;

    /// <summary>Attempt TNEF (winmail.dat) text/attachment extraction. Never involves crypto.</summary>
    public bool ExtractTnef { get; init; } = true;

    /// <summary>Include text/calendar bodies in the FTS text.</summary>
    public bool IncludeCalendarText { get; init; } = true;
}
