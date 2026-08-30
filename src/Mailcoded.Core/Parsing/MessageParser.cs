using System.Buffers;
using System.Text;
using Mailcoded.Core.Domain.Primitives;
using MimeKit;
using MimeKit.Tnef;
using MimeKit.Utils;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// Raw RFC822 -> <see cref="ParsedMessage"/>. The only place MimeKit is allowed to appear on the
/// read path (SPEC invariant 8). Parsing is streaming and bounded: nothing here reads a whole
/// attachment, and no malformed input escapes as an exception.
/// </summary>
public sealed class MessageParser
{
    private readonly MessageParserOptions _options;
    private readonly ParserOptions _mimeOptions;

    public MessageParser() : this(MessageParserOptions.Default)
    {
    }

    public MessageParser(MessageParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        _mimeOptions = ParserOptions.Default.Clone();
        _mimeOptions.CharsetEncoding = MimeCharsets.FallbackEncoding;
    }

    public static MessageParser Default { get; } = new();

    public MessageParserOptions Options => _options;

    /// <param name="internalDateUtc">
    /// IMAP INTERNALDATE, used when the Date header is missing or absurd (RELIABILITY edge case 18).
    /// Pass <c>default</c> when the caller has none.
    /// </param>
    public ParsedMessage Parse(Stream raw, DateTimeOffset internalDateUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ct.ThrowIfCancellationRequested();

        MimeMessage message;
        try
        {
            message = MimeMessage.Load(_mimeOptions, raw, raw.CanSeek, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unparsable(internalDateUtc, ex);
        }

        using (message)
        {
            return ToParsedMessage(message, internalDateUtc, ct);
        }
    }

    public ParsedMessage Parse(byte[] raw, DateTimeOffset internalDateUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        using var stream = new MemoryStream(raw, 0, raw.Length, writable: false, publiclyVisible: false);
        return Parse(stream, internalDateUtc, ct);
    }

    public async Task<ParsedMessage> ParseAsync(Stream raw, DateTimeOffset internalDateUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ct.ThrowIfCancellationRequested();

        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(_mimeOptions, raw, raw.CanSeek, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unparsable(internalDateUtc, ex);
        }

        using (message)
        {
            return ToParsedMessage(message, internalDateUtc, ct);
        }
    }

    /// <summary>
    /// Parses and computes blob identity in one traversal of <paramref name="raw"/>. A seekable
    /// source is hashed then rewound so MimeKit can bind part content without copying it; a
    /// forward-only source is teed through <see cref="HashingReadStream"/> instead.
    /// </summary>
    public ParsedRawMessage ParseAndHash(Stream raw, DateTimeOffset internalDateUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.CanSeek)
        {
            var start = raw.Position;
            var hex = RawMessageHasher.ComputeSha256Hex(raw, ct);
            var size = raw.Position - start;
            raw.Position = start;
            return new ParsedRawMessage(Parse(raw, internalDateUtc, ct), hex, size);
        }

        using var tee = new HashingReadStream(raw);
        var parsed = Parse(tee, internalDateUtc, ct);
        tee.DrainRemainder(ct);
        return new ParsedRawMessage(parsed, tee.GetHashHex(), tee.BytesRead);
    }

    /// <summary>
    /// Streams one attachment's decoded content into <paramref name="destination"/>. The index is
    /// the one <see cref="ParsedAttachment.Index"/> carries, assigned by the same walk, so a stored
    /// envelope and a later fetch agree. Returns null when the index does not exist or the part
    /// cannot be decoded; nothing is buffered, so a 100 MB attachment costs a copy buffer.
    /// </summary>
    public ParsedAttachment? CopyAttachmentTo(Stream raw, int index, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(destination);
        if (index < 0) return null;

        MimeMessage message;
        try
        {
            message = MimeMessage.Load(_mimeOptions, raw, raw.CanSeek, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        using (message)
        {
            var walk = new BodyWalk(new List<string>()) { SkipText = true };
            try
            {
                Walk(message.Body, 0, walk, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }

            if (index >= walk.Entities.Count) return null;

            try
            {
                switch (walk.Entities[index])
                {
                    case MessagePart nested when nested.Message is { } inner:
                        inner.WriteTo(destination, ct);
                        break;

                    case MimePart part when part.Content is { } content:
                        content.DecodeTo(destination, ct);
                        break;

                    default:
                        return null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }

            return walk.Attachments[index];
        }
    }

    private ParsedMessage ToParsedMessage(MimeMessage message, DateTimeOffset internalDateUtc, CancellationToken ct)
    {
        var warnings = new List<string>();

        var messageId = ResolveMessageId(message, warnings);
        var references = ResolveReferences(message);
        var inReplyTo = MessageId.TryParse(message.InReplyTo, out var irt) ? irt : (MessageId?)null;
        var date = ResolveDate(message, internalDateUtc, warnings);

        var walk = new BodyWalk(warnings);
        try
        {
            Walk(message.Body, 0, walk, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            walk.Warn("body-walk-failed:" + ex.GetType().Name);
        }

        var bodyText = walk.Text.Length > 0
            ? PlainText.Normalize(walk.Text.ToString(), _options.MaxBodyTextChars)
            : string.Empty;

        if (bodyText.Length == 0 && walk.Html is not null)
            bodyText = HtmlToText.Convert(walk.Html, _options.MaxBodyTextChars);

        if (bodyText.Length == 0 && walk.Html is null && walk.Attachments.Count == 0)
            warnings.Add("empty-body");

        return new ParsedMessage
        {
            MessageId = messageId,
            References = references,
            InReplyTo = inReplyTo,
            Subject = PlainText.NormalizeHeader(message.Subject, _options.MaxHeaderChars),
            From = FormatFrom(message),
            To = FormatAddresses(message.To),
            Cc = FormatAddresses(message.Cc),
            ReplyTo = FormatAddresses(message.ReplyTo),
            DateUtc = date,
            BodyText = bodyText,
            BodyHtml = walk.Html,
            Attachments = walk.Attachments,
            ParseWarnings = warnings,
        };
    }

    private MessageId? ResolveMessageId(MimeMessage message, List<string> warnings)
    {
        var headerCount = 0;
        foreach (var header in message.Headers)
            if (header.Id == HeaderId.MessageId)
                headerCount++;

        if (headerCount > 1) warnings.Add("duplicate-message-id");

        if (MessageId.TryParse(message.MessageId, out var id)) return id;

        warnings.Add(headerCount == 0 ? "missing-message-id" : "unparsable-message-id");
        return null;
    }

    private static IReadOnlyList<MessageId> ResolveReferences(MimeMessage message)
    {
        List<MessageId>? list = null;
        var seen = 0;

        foreach (var raw in message.References)
        {
            if (++seen > 64) break;
            if (!MessageId.TryParse(raw, out var id)) continue;
            list ??= new List<MessageId>();
            if (!list.Contains(id)) list.Add(id);
        }

        return list is null ? Array.Empty<MessageId>() : list;
    }

    private static DateTimeOffset ResolveDate(MimeMessage message, DateTimeOffset internalDateUtc, List<string> warnings)
    {
        var hasInternal = internalDateUtc != default;
        var header = message.Headers[HeaderId.Date];

        if (!string.IsNullOrEmpty(header) && DateUtils.TryParse(header, out var parsed))
        {
            var utc = parsed.ToUniversalTime();
            if (utc.Year >= 1971 && (!hasInternal || utc <= internalDateUtc.ToUniversalTime().AddDays(2)))
                return utc;

            warnings.Add("implausible-date");
        }
        else
        {
            warnings.Add(string.IsNullOrEmpty(header) ? "missing-date" : "unparsable-date");
        }

        return hasInternal ? internalDateUtc.ToUniversalTime() : DateTimeOffset.UnixEpoch;
    }

    private string? FormatFrom(MimeMessage message)
    {
        var from = FormatAddresses(message.From);
        if (from is not null) return from;

        var sender = message.Sender;
        return sender is null ? null : PlainText.NormalizeHeader(sender.ToString(), _options.MaxHeaderChars);
    }

    private string? FormatAddresses(InternetAddressList? list)
    {
        if (list is null || list.Count == 0) return null;
        return PlainText.NormalizeHeader(list.ToString(), _options.MaxHeaderChars);
    }

    private void Walk(MimeEntity? entity, int depth, BodyWalk walk, CancellationToken ct)
    {
        if (entity is null) return;
        ct.ThrowIfCancellationRequested();

        if (++walk.Visited > _options.MaxParts)
        {
            walk.Warn("too-many-parts");
            return;
        }

        if (depth > _options.MaxDepth)
        {
            walk.Warn("mime-too-deep");
            return;
        }

        switch (entity)
        {
            case Multipart multipart:
                foreach (var child in multipart) Walk(child, depth + 1, walk, ct);
                return;

            case MessagePart nested:
                AddAttachment(nested, walk);
                Walk(nested.Message?.Body, depth + 1, walk, ct);
                return;

            case TnefPart tnef when _options.ExtractTnef:
                AddAttachment(tnef, walk);
                MimeMessage? converted = null;
                try
                {
                    converted = tnef.ConvertToMessage();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    walk.Warn("tnef-unreadable:" + ex.GetType().Name);
                }

                if (converted is not null)
                {
                    using (converted) Walk(converted.Body, depth + 1, walk, ct);
                }

                return;

            case MimePart part:
                WalkLeaf(part, walk);
                return;

            default:
                return;
        }
    }

    private void WalkLeaf(MimePart part, BodyWalk walk)
    {
        var subtype = part.ContentType.MediaSubtype;

        if (IsOpaqueCrypto(part))
        {
            walk.Warn("encrypted-part-skipped");
            AddAttachment(part, walk);
            return;
        }

        if (part is TextPart text && !part.IsAttachment && string.IsNullOrEmpty(FileNameOf(part)) && IsBodyMedia(subtype))
        {
            if (walk.SkipText) return;

            var body = ReadText(text, walk);

            if (subtype.Equals("html", StringComparison.OrdinalIgnoreCase))
            {
                if (walk.Html is null && body.Length > 0)
                    walk.Html = body.Length > _options.MaxHtmlChars ? body[.._options.MaxHtmlChars] : body;
                return;
            }

            if (walk.Text.Length < _options.MaxBodyTextChars && body.Length > 0)
            {
                if (walk.Text.Length > 0) walk.Text.Append('\n');
                var room = _options.MaxBodyTextChars - walk.Text.Length;
                walk.Text.Append(body.Length > room ? body.AsSpan(0, room) : body.AsSpan());
            }

            return;
        }

        AddAttachment(part, walk);
    }

    private void AddAttachment(MimeEntity entity, BodyWalk walk)
    {
        if (walk.Attachments.Count >= _options.MaxAttachments)
        {
            walk.Warn("too-many-attachments");
            return;
        }

        var disposition = entity.ContentDisposition?.Disposition;
        var contentId = NormalizeContentId(entity.ContentId);
        var inline = disposition is not null
            ? disposition.Equals("inline", StringComparison.OrdinalIgnoreCase)
            : contentId is not null;

        walk.Entities.Add(entity);
        walk.Attachments.Add(new ParsedAttachment
        {
            Index = walk.Attachments.Count,
            MimeType = MimeTypeOf(entity),
            FileName = RawFileName(FileNameOf(entity)),
            ContentId = contentId,
            Size = SizeOf(entity),
            IsInline = inline,
        });
    }

    private string ReadText(TextPart part, BodyWalk walk)
    {
        var content = part.Content;
        if (content is null) return string.Empty;

        var encoding = MimeCharsets.Resolve(part.ContentType.Charset);
        var cap = Math.Max(_options.MaxPartBytes, 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(cap, RawMessageHasher.BufferSize));

        try
        {
            using var stream = content.Open();
            var total = 0;
            while (total < cap)
            {
                var read = stream.Read(buffer, total, cap - total);
                if (read <= 0) break;
                total += read;
            }

            if (total >= cap && stream.ReadByte() >= 0) walk.Warn("body-part-truncated");

            return encoding.GetString(buffer, 0, total);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            walk.Warn("body-part-unreadable:" + ex.GetType().Name);
            return string.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool IsBodyMedia(string subtype) =>
        subtype.Equals("plain", StringComparison.OrdinalIgnoreCase) ||
        subtype.Equals("html", StringComparison.OrdinalIgnoreCase) ||
        subtype.Equals("enriched", StringComparison.OrdinalIgnoreCase) ||
        subtype.Equals("markdown", StringComparison.OrdinalIgnoreCase) ||
        (_options.IncludeCalendarText && subtype.Equals("calendar", StringComparison.OrdinalIgnoreCase));

    /// <summary>Opaque crypto is catalogued and skipped. mailcoded never decrypts or verifies (CLAUDE.md invariant 2).</summary>
    private static bool IsOpaqueCrypto(MimePart part)
    {
        var subtype = part.ContentType.MediaSubtype;
        return subtype.Equals("pkcs7-mime", StringComparison.OrdinalIgnoreCase) ||
               subtype.Equals("x-pkcs7-mime", StringComparison.OrdinalIgnoreCase) ||
               subtype.Equals("pgp-encrypted", StringComparison.OrdinalIgnoreCase);
    }

    private static string MimeTypeOf(MimeEntity entity)
    {
        var type = entity.ContentType.MimeType;
        if (string.IsNullOrWhiteSpace(type)) return "application/octet-stream";
        return type.Length > 128 ? type[..128] : type;
    }

    private static string? FileNameOf(MimeEntity entity)
    {
        if (entity is MimePart part && !string.IsNullOrEmpty(part.FileName)) return part.FileName;

        var disposition = entity.ContentDisposition?.FileName;
        if (!string.IsNullOrEmpty(disposition)) return disposition;

        var name = entity.ContentType.Name;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Kept verbatim apart from control characters, which are never legitimate in a filename and
    /// would let a sender forge a line in a log or a UI row. Path separators and Windows device
    /// names survive on purpose: sanitizing for a filesystem is
    /// <see cref="AttachmentNaming.ToSaveAsFileName"/>'s job, at the point of saving.
    /// </summary>
    private static string? RawFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var source = name.Length > 1024 ? name[..1024] : name;
        var sb = new StringBuilder(source.Length);
        foreach (var c in source)
            if (!char.IsControl(c))
                sb.Append(c);

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? NormalizeContentId(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId)) return null;

        var s = contentId.Trim();
        if (s.StartsWith('<') && s.EndsWith('>') && s.Length > 2) s = s[1..^1].Trim();
        if (s.Length == 0 || s.Length > 512) return null;

        foreach (var c in s)
            if (char.IsControl(c) || c is '<' or '>')
                return null;

        return s;
    }

    /// <summary>Size comes from the part's bounded content stream; the content itself is never read.</summary>
    private static long SizeOf(MimeEntity entity)
    {
        if (entity is not MimePart part || part.Content is not { } content) return 0;

        try
        {
            var stream = content.Stream;
            if (stream is null) return 0;

            var encoded = stream.Length;
            if (encoded <= 0) return 0;
            return part.ContentTransferEncoding == ContentEncoding.Base64 ? encoded / 4 * 3 : encoded;
        }
        catch (NotSupportedException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static ParsedMessage Unparsable(DateTimeOffset internalDateUtc, Exception ex) => new()
    {
        DateUtc = internalDateUtc == default ? DateTimeOffset.UnixEpoch : internalDateUtc.ToUniversalTime(),
        ParseWarnings = ["unparsable-message:" + ex.GetType().Name],
    };

    private sealed class BodyWalk(List<string> warnings)
    {
        public StringBuilder Text { get; } = new();
        public string? Html { get; set; }
        public List<ParsedAttachment> Attachments { get; } = [];

        /// <summary>Parallel to <see cref="Attachments"/>: index i describes entity i.</summary>
        public List<MimeEntity> Entities { get; } = [];

        public int Visited { get; set; }

        /// <summary>Set when only attachment identity is wanted, so no body part is decoded.</summary>
        public bool SkipText { get; init; }

        public void Warn(string warning)
        {
            if (warnings.Count < 32 && !warnings.Contains(warning)) warnings.Add(warning);
        }
    }
}

/// <summary>A parsed message together with the blob identity of the bytes it came from.</summary>
public sealed record ParsedRawMessage(ParsedMessage Message, string Sha256Hex, long RawSize);
