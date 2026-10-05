using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public static class AttachmentTextIndexing
{
    // Bumping this re-queues every message indexed by an older extractor.
    public const int ExtractorVersion = 1;

    public static IReadOnlyList<AttachmentTextEntry> EntriesOf(ParsedMessage parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.AttachmentTexts.Count == 0) return [];

        var entries = new List<AttachmentTextEntry>(parsed.AttachmentTexts.Count);
        foreach (var text in parsed.AttachmentTexts)
        {
            entries.Add(new AttachmentTextEntry
            {
                Index = text.Index,
                MimeType = text.MimeType,
                FileName = text.FileName,
                Text = text.Text,
                Truncated = text.Truncated,
            });
        }

        return entries;
    }
}
