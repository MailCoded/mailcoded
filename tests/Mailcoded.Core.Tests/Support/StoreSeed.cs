using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Tests.Support;

/// <summary>Builds the account/folder/message rows a store test needs, with no hidden defaults.</summary>
public static class StoreSeed
{
    public static readonly DateTimeOffset BaseDate =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static AccountConfig AccountConfigFor(string email) => new()
    {
        Email = email,
        DisplayName = "Test Account",
        Provider = ProviderKind.Imap,
        Imap = new ImapConfig
        {
            Host = "imap.example.com",
            Port = 993,
            Security = SecureSocket.SslOnConnect,
            Username = email,
            WatchFolders = ["INBOX", "Archive"],
        },
        Smtp = new SmtpConfig
        {
            Host = "smtp.example.com",
            Port = 587,
            Security = SecureSocket.StartTls,
            Username = email,
        },
        Auth = AuthKind.Password,
        SecretRef = "mailcoded:imap:" + email,
        Quirks = new ServerQuirksConfig { Latched = ServerQuirks.None },
    };

    public static Task<AccountId> AccountAsync(
        SqliteStore store,
        CancellationToken ct,
        string email = "tester@example.com") =>
        store.AddAccountAsync(AccountConfigFor(email), ct);

    public static async Task<FolderId> FolderAsync(
        SqliteStore store,
        AccountId accountId,
        string name,
        FolderRole role,
        CancellationToken ct)
    {
        var ids = await store.UpsertFoldersAsync(
            accountId,
            [new RemoteFolder { Path = FolderPath.Create(name), Role = role }],
            ct);

        return ids[0];
    }

    public static RemoteEnvelope Envelope(
        uint uid,
        string? subject = null,
        string from = "alice@example.com",
        string to = "bob@example.org",
        string? cc = null,
        DateTimeOffset date = default,
        MessageFlags flags = MessageFlags.Unread,
        string? messageId = null,
        IReadOnlyList<string>? references = null,
        string? inReplyTo = null,
        bool hasAttachments = false,
        long size = 2048,
        ulong modSeq = 0) => new()
        {
            Uid = new Uid(uid),
            Flags = flags,
            ModSeq = new ModSeq(modSeq),
            MessageIdHeader = messageId ?? $"msg-{uid}@example.com",
            References = references ?? Array.Empty<string>(),
            InReplyTo = inReplyTo,
            Subject = subject ?? $"Message {uid}",
            From = from,
            To = to,
            Cc = cc,
            DateUtc = date == default ? BaseDate.AddMinutes(uid) : date,
            Size = size,
            HasAttachments = hasAttachments,
        };

    public static async Task<LocalMessageId> MessageAsync(
        SqliteStore store,
        FolderId folderId,
        RemoteEnvelope envelope,
        CancellationToken ct,
        string? bodyText = null,
        IReadOnlyList<Tag>? tags = null,
        IThreader? threader = null)
    {
        await store.IngestEnvelopesAsync(folderId, [envelope], threader, ct);

        var id = Require.Value(store.FindMessage(folderId, envelope.Uid, ct), "the ingested message id");

        if (bodyText is not null)
            await store.SetBodyTextAsync(id, bodyText, null, envelope.HasAttachments, ct);

        if (tags is { Count: > 0 })
            await store.SetTagsAsync(id, tags, ct);

        return id;
    }

    public static IReadOnlyList<Tag> Tags(params string[] values)
    {
        var tags = new List<Tag>(values.Length);
        foreach (var value in values) tags.Add(Tag.Parse(value));
        return tags;
    }
}
