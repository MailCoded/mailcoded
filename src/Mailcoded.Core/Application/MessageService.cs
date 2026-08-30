using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public enum MessageBodyFormat
{
    Text,
    Html,
    Raw,
}

public sealed record MessageServiceOptions
{
    /// <summary>Largest attachment this layer will materialize for a base64 RPC response.</summary>
    public long MaxAttachmentBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Largest raw message this layer will materialize when HTML or raw is asked for.</summary>
    public long MaxRawBytes { get; init; } = 64L * 1024 * 1024;

    public int ThreadPageSize { get; init; } = 500;

    public static readonly MessageServiceOptions Default = new();
}

/// <summary>One message as the reader needs it. <see cref="BodyHtml"/> is raw; the client sanitizes.</summary>
public sealed record MessageView
{
    public required EnvelopeRow Envelope { get; init; }
    public IReadOnlyList<Tag> Tags { get; init; } = [];
    public string? BodyText { get; init; }
    public string? BodyHtml { get; init; }
    public byte[]? Raw { get; init; }
    public IReadOnlyList<ParsedAttachment> Attachments { get; init; } = [];
    public IReadOnlyList<string> ParseWarnings { get; init; } = [];
    public bool BodyFetched { get; init; }

    /// <summary>True when this call performed the lazy fetch rather than reading a stored body.</summary>
    public bool FetchedNow { get; init; }
}

public sealed record ThreadView
{
    public required ThreadKey ThreadKey { get; init; }
    public IReadOnlyList<EnvelopeRow> Messages { get; init; } = [];
    public IReadOnlyDictionary<LocalMessageId, IReadOnlyList<Tag>> Tags { get; init; } =
        new Dictionary<LocalMessageId, IReadOnlyList<Tag>>();
}

public sealed record AttachmentContent
{
    public required int Index { get; init; }
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public required byte[] Content { get; init; }
    public long Size { get; init; }
    public bool IsInline { get; init; }
}

public sealed record TagsSetResult
{
    public required IReadOnlyList<Tag> Tags { get; init; }
    public MessageFlags Flags { get; init; }
    public bool PushedToServer { get; init; }
}

/// <summary>message.get with lazy body fetch, thread.get, attachment.get, message.move, tags.set.</summary>
public sealed class MessageService
{
    private const int FetchBufferSize = 64 * 1024;

    private readonly SqliteStore _store;
    private readonly MessageParser _parser;
    private readonly AuditLog _audit;
    private readonly AgentPolicy _policy;
    private readonly MessageServiceOptions _options;

    public MessageService(
        SqliteStore store,
        MessageParser parser,
        AuditLog audit,
        AgentPolicy policy,
        MessageServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(policy);

        _store = store;
        _parser = parser;
        _audit = audit;
        _policy = policy;
        _options = options ?? MessageServiceOptions.Default;
    }

    public EnvelopeRow GetEnvelope(LocalMessageId id, CancellationToken ct = default) => Require(id, ct);

    /// <summary>Fetches the raw message on demand, stores the blob, fills body text, upgrades FTS.</summary>
    public async Task<MessageView> GetAsync(
        IMailProvider? provider,
        LocalMessageId id,
        MessageBodyFormat format,
        bool fetchIfMissing,
        CallerContext caller,
        CancellationToken ct)
    {
        if (format is (MessageBodyFormat.Html or MessageBodyFormat.Raw) && !_policy.AllowsHtmlBody(caller.Kind))
        {
            throw new PolicyDeniedException(
                PolicyDenialReason.HtmlBodyDenied,
                "Agents receive plaintext bodies only.");
        }

        var envelope = Require(id, ct);
        var fetchedNow = false;

        if (!envelope.BodyFetched && fetchIfMissing && provider is not null)
        {
            envelope = await FetchBodyAsync(provider, envelope, caller, ct).ConfigureAwait(false);
            fetchedNow = true;
        }

        var tags = ReadTagView(envelope, ct);
        var bodyText = _store.GetBodyText(id, ct);

        if (format == MessageBodyFormat.Text)
        {
            return new MessageView
            {
                Envelope = envelope,
                Tags = tags,
                BodyText = bodyText,
                BodyFetched = envelope.BodyFetched,
                FetchedNow = fetchedNow,
            };
        }

        var raw = ReadRaw(envelope, ct);
        if (raw is null)
        {
            return new MessageView
            {
                Envelope = envelope,
                Tags = tags,
                BodyText = bodyText,
                BodyFetched = envelope.BodyFetched,
                FetchedNow = fetchedNow,
            };
        }

        var parsed = _parser.Parse(raw, envelope.DateUtc, ct);

        return new MessageView
        {
            Envelope = envelope,
            Tags = tags,
            BodyText = string.IsNullOrEmpty(parsed.BodyText) ? bodyText : parsed.BodyText,
            BodyHtml = format == MessageBodyFormat.Html ? parsed.BodyHtml : null,
            Raw = format == MessageBodyFormat.Raw ? raw : null,
            Attachments = parsed.Attachments,
            ParseWarnings = parsed.ParseWarnings,
            BodyFetched = envelope.BodyFetched,
            FetchedNow = fetchedNow,
        };
    }

    public ThreadView GetThread(ThreadKey threadKey, int limit = 0, CancellationToken ct = default)
    {
        var pageSize = limit <= 0 ? _options.ThreadPageSize : limit;
        var messages = _store.ListThreadMessages(threadKey, pageSize, ct);

        var ids = new List<LocalMessageId>(messages.Count);
        foreach (var message in messages) ids.Add(message.Id);

        return new ThreadView
        {
            ThreadKey = threadKey,
            Messages = messages,
            Tags = _store.GetTagsFor(ids, ct),
        };
    }

    public async Task<AttachmentContent> GetAttachmentAsync(
        IMailProvider? provider,
        LocalMessageId id,
        int index,
        CallerContext caller,
        CancellationToken ct)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));

        var envelope = Require(id, ct);
        if (!envelope.BodyFetched && provider is not null)
            envelope = await FetchBodyAsync(provider, envelope, caller, ct).ConfigureAwait(false);

        var raw = ReadRaw(envelope, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No stored body for message {id.Value}.");

        using var source = new MemoryStream(raw, writable: false);
        using var destination = new MemoryStream();

        var attachment = _parser.CopyAttachmentTo(source, index, destination, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"Message {id.Value} has no attachment at index {index}.");

        if (destination.Length > _options.MaxAttachmentBytes)
        {
            throw new StoreException(
                FailureCategory.Full,
                $"Attachment is {destination.Length} bytes; the transfer limit is {_options.MaxAttachmentBytes} bytes.");
        }

        await _audit.InfoAsync(
            AuditEvents.AttachmentRead,
            envelope.AccountId,
            caller,
            AuditText.Fields(("message", AuditText.Number(id.Value)), ("index", AuditText.Number(index))),
            ct).ConfigureAwait(false);

        return new AttachmentContent
        {
            Index = attachment.Index,
            FileName = AttachmentNaming.ToSaveAsFileName(attachment.FileName, attachment.Index, attachment.MimeType),
            MimeType = attachment.MimeType,
            Content = destination.ToArray(),
            Size = destination.Length,
            IsInline = attachment.IsInline,
        };
    }

    public async Task MoveAsync(
        IMailProvider? provider,
        LocalMessageId id,
        FolderId toFolderId,
        CallerContext caller,
        CancellationToken ct)
    {
        if (!_policy.AllowsMove(caller.Kind))
        {
            throw new PolicyDeniedException(
                PolicyDenialReason.OperationNotAvailable,
                "Moving mail is not part of the agent surface.");
        }

        var envelope = Require(id, ct);
        var target = _store.GetFolder(toFolderId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {toFolderId.Value}.");
        var source = _store.GetFolder(envelope.FolderId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {envelope.FolderId.Value}.");

        Uid? newUid = null;
        if (provider is not null && envelope.Uid is { } uid)
        {
            newUid = await provider.MoveAsync(
                new FolderRef(source.Id, source.Path),
                uid,
                new FolderRef(target.Id, target.Path),
                ct).ConfigureAwait(false);
        }

        await _store.MoveMessageAsync(id, toFolderId, newUid, ct).ConfigureAwait(false);

        await _audit.InfoAsync(
            AuditEvents.MessageMoved,
            envelope.AccountId,
            caller,
            AuditText.Fields(
                ("message", AuditText.Number(id.Value)),
                ("from", source.Path.Value),
                ("to", target.Path.Value)),
            ct).ConfigureAwait(false);
    }

    /// <summary>Merges the delta, writes tags locally, pushes the projected flags, then audits.</summary>
    public async Task<TagsSetResult> SetTagsAsync(
        IMailProvider? provider,
        LocalMessageId id,
        TagDelta delta,
        CallerContext caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var envelope = Require(id, ct);
        var folder = _store.GetFolder(envelope.FolderId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {envelope.FolderId.Value}.");

        var state = _store.LoadFolderState(envelope.FolderId, includeKnownUids: false, ct);
        var acceptsKeywords = state?.ServerAcceptsCustomKeywords ?? true;

        var current = ReadTagView(envelope, ct);
        var desired = TagFlagMap.Apply(current, delta);
        var flagDelta = TagFlagMap.Project(delta, acceptsKeywords);

        var pushed = false;
        if (provider is not null && !flagDelta.IsEmpty && envelope.Uid is { } uid)
        {
            await provider.SetFlagsAsync(new FolderRef(folder.Id, folder.Path), uid, flagDelta, ct).ConfigureAwait(false);
            pushed = true;
        }

        var flags = (envelope.Flags | flagDelta.Add) & ~flagDelta.Remove;
        if (flags != envelope.Flags && envelope.Uid is { } localUid)
        {
            await _store.ApplyFlagChangesAsync(
                envelope.FolderId,
                [new FlagUpdate(localUid, flags, envelope.ModSeq)],
                ct).ConfigureAwait(false);
        }

        var custom = new List<Tag>(desired.Count);
        foreach (var tag in desired)
        {
            if (TagFlagMap.SystemFlagFor(tag) is not null) continue;
            custom.Add(tag);
        }

        await _store.SetTagsAsync(id, custom, ct).ConfigureAwait(false);

        var resulting = TagFlagMap.ToTags(flags, ToKeywordStrings(custom));
        await _audit.TagsAsync(envelope.AccountId, caller, id, delta, resulting, ct).ConfigureAwait(false);

        return new TagsSetResult { Tags = resulting, Flags = flags, PushedToServer = pushed };
    }

    /// <summary>Streams the fetch through a staging file so a 100 MB message never becomes a byte[].</summary>
    private async Task<EnvelopeRow> FetchBodyAsync(
        IMailProvider provider,
        EnvelopeRow envelope,
        CallerContext caller,
        CancellationToken ct)
    {
        if (envelope.Uid is not { } uid) return envelope;

        var folder = _store.GetFolder(envelope.FolderId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {envelope.FolderId.Value}.");

        var staging = Path.Combine(_store.BlobDirectory, "fetch-" + Guid.NewGuid().ToString("N") + ".tmp");
        BlobRef blob;
        ParsedMessage parsed;

        try
        {
            await using (var destination = Create(staging))
            {
                await provider.FetchRawMessageToAsync(new FolderRef(folder.Id, folder.Path), uid, destination, ct)
                    .ConfigureAwait(false);
            }

            await using (var source = OpenRead(staging))
            {
                blob = await _store.StoreBlobAsync(source, ct).ConfigureAwait(false);
            }

            await using (var source = OpenRead(staging))
            {
                parsed = await _parser.ParseAsync(source, envelope.DateUtc, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(staging);
        }

        await _store.SetBodyTextAsync(envelope.Id, parsed.BodyText, blob.Id, parsed.HasAttachments, ct)
            .ConfigureAwait(false);

        await _audit.InfoAsync(
            AuditEvents.BodyFetched,
            envelope.AccountId,
            caller,
            AuditText.Fields(
                ("message", AuditText.Number(envelope.Id.Value)),
                ("bytes", AuditText.Number(blob.Size)),
                ("warnings", parsed.ParseWarnings.Count > 0 ? AuditText.Number(parsed.ParseWarnings.Count) : null)),
            ct).ConfigureAwait(false);

        return _store.GetEnvelope(envelope.Id, ct) ?? (envelope with { BodyFetched = true, BlobId = blob.Id });
    }

    private static FileStream Create(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FetchBufferSize, useAsync: true);

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, FetchBufferSize, useAsync: true);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private byte[]? ReadRaw(EnvelopeRow envelope, CancellationToken ct)
    {
        if (envelope.BlobId is not { } blobId) return null;

        var reference = _store.GetBlob(blobId, ct);
        if (reference is null) return null;
        if (reference.Size > _options.MaxRawBytes)
        {
            throw new StoreException(
                FailureCategory.Full,
                $"Stored message is {reference.Size} bytes; the transfer limit is {_options.MaxRawBytes} bytes.");
        }

        return _store.ReadBlobBytes(blobId, ct);
    }

    private IReadOnlyList<Tag> ReadTagView(EnvelopeRow envelope, CancellationToken ct)
    {
        var custom = _store.GetTags(envelope.Id, ct);
        return TagFlagMap.ToTags(envelope.Flags, ToKeywordStrings(custom));
    }

    private static IReadOnlyList<string> ToKeywordStrings(IReadOnlyList<Tag> tags)
    {
        if (tags.Count == 0) return [];

        var keywords = new List<string>(tags.Count);
        foreach (var tag in tags) keywords.Add(tag.Value);
        return keywords;
    }

    private EnvelopeRow Require(LocalMessageId id, CancellationToken ct) =>
        _store.GetEnvelope(id, ct)
        ?? throw new StoreException(FailureCategory.NotFound, $"No message with id {id.Value}.");
}
