using MailKit;
using Mailcoded.Core.Domain.Signals;
using ContentType = MimeKit.ContentType;
using HeaderList = MimeKit.HeaderList;

namespace Mailcoded.Core.Providers;

public sealed record EnvelopeSignals(SignalHeaders? Headers, bool? Automated, bool? Encrypted);

/// <summary>Turns what a FETCH returned into MimeKit-free facts; all judgement lives in <see cref="MessageSignals"/>.</summary>
public static class ImapEnvelopeSignals
{
    private const int MaxStructureNodes = 64;
    private const int MaxStructureDepth = 8;

    public static EnvelopeSignals Read(IMessageSummary summary, string? from)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var headers = summary.Headers is { } fetched ? HeadersFrom(fetched) : null;
        ContentShape? top = summary.Body is { } body ? ShapeOf(body.ContentType) : null;

        return new EnvelopeSignals(
            headers,
            MessageSignals.IsAutomated(headers, from, top),
            summary.Body is null || !HasEncryptedPart(summary.Body) ? null : true);
    }

    private static SignalHeaders HeadersFrom(HeaderList headers)
    {
        var collector = new SignalHeaderCollector();
        foreach (var header in headers)
            if (SignalHeaderCollector.Wants(header.Field)) collector.Add(header.Field, header.Value);

        return collector.Build();
    }

    private static ContentShape ShapeOf(ContentType type) =>
        new(type.MediaType, type.MediaSubtype, Parameter(type, "protocol"), Parameter(type, "smime-type"), Parameter(type, "report-type"));

    private static string? Parameter(ContentType type, string name) =>
        type.Parameters.TryGetValue(name, out string? value) ? value : null;

    // Never false: inline armor lives in the text body, and a structure walk that ran out of budget saw only part of the tree.
    // Attached messages are not descended into: a forwarded encrypted message does not make the outer one unreadable.
    private static bool HasEncryptedPart(BodyPart root)
    {
        var budget = MaxStructureNodes;
        return HasEncryptedPart(root, 0, ref budget);
    }

    private static bool HasEncryptedPart(BodyPart part, int depth, ref int budget)
    {
        if (--budget < 0 || depth > MaxStructureDepth) return false;
        if (MessageSignals.IsEncrypted(ShapeOf(part.ContentType))) return true;
        if (part is not BodyPartMultipart multipart) return false;

        foreach (var child in multipart.BodyParts)
            if (HasEncryptedPart(child, depth + 1, ref budget)) return true;

        return false;
    }
}
