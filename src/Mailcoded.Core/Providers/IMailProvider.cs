using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Providers;

/// <summary>
/// The one port to a remote mail service. MailKit types never cross this boundary
/// (SPEC invariant 8) — an implementation translates to Domain types on the way out.
/// </summary>
public interface IMailProvider : IAsyncDisposable
{
    ServerCaps Capabilities { get; }
    bool IsConnected { get; }

    Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct);

    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct);

    /// <summary>Opens the folder and reports what the server says about it, without fetching anything.</summary>
    Task<ServerFolderInfo> OpenFolderAsync(FolderRef folder, bool writable, CancellationToken ct);

    /// <summary>
    /// Streams the changes the <paramref name="plan"/> asked for. Emits
    /// <see cref="SyncEvent.BatchComplete"/> at every durable checkpoint so the caller can
    /// commit a transaction per batch.
    /// </summary>
    IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderRef folder, SyncPlan plan, CancellationToken ct);

    /// <summary>
    /// Convenience form for small messages. Prefer <see cref="FetchRawMessageToAsync"/> — a
    /// 100 MB attachment must never become a byte[] on the managed heap (RELIABILITY §14.2).
    /// </summary>
    Task<byte[]> FetchRawMessageAsync(FolderRef folder, Uid uid, CancellationToken ct);

    /// <summary>Streams the raw RFC822 message into <paramref name="destination"/>.</summary>
    Task FetchRawMessageToAsync(FolderRef folder, Uid uid, Stream destination, CancellationToken ct);

    Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct);

    Task<Uid?> MoveAsync(FolderRef from, Uid uid, FolderRef to, CancellationToken ct);

    /// <summary>Appends a raw RFC822 message, used to place a sent copy in Sent.</summary>
    Task<Uid?> AppendAsync(FolderRef folder, byte[] raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct);

    /// <summary>Streaming form of <see cref="AppendAsync(FolderRef, byte[], MessageFlags, DateTimeOffset, CancellationToken)"/>.</summary>
    Task<Uid?> AppendAsync(FolderRef folder, Stream raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct);

    /// <summary>Blocks in IMAP IDLE, invoking <paramref name="onChange"/> on each notification.</summary>
    Task WatchAsync(FolderRef folder, Func<CancellationToken, Task> onChange, CancellationToken ct);
}

/// <summary>Sending is a separate capability so a read-only provider need not implement it.</summary>
public interface IMailSender : IAsyncDisposable
{
    Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct);

    /// <summary>
    /// Submits a raw RFC822 message. Returns the server's response line for the audit record.
    /// The caller owns idempotency via the pre-assigned Message-ID.
    /// </summary>
    Task<string> SendAsync(byte[] raw, EmailAddress from, IReadOnlyList<EmailAddress> recipients, CancellationToken ct);

    /// <summary>Largest message the server will accept, from the SIZE capability, or null if unadvertised.</summary>
    long? MaxMessageSize { get; }

    bool SupportsSmtpUtf8 { get; }
}

/// <summary>Identifies a folder to the provider by both its local id and its server path.</summary>
public readonly record struct FolderRef(FolderId Id, FolderPath Path)
{
    public override string ToString() => Path.Value;
}

/// <summary>A folder as the server lists it.</summary>
public sealed record RemoteFolder
{
    public required FolderPath Path { get; init; }
    public FolderRole Role { get; init; } = FolderRole.None;
    public bool IsSelectable { get; init; } = true;
    public int TotalCount { get; init; }
    public int UnreadCount { get; init; }
}
