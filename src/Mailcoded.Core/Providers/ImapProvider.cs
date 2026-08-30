using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Secrets;
using ImapFlags = MailKit.MessageFlags;
using MailcodedFlags = Mailcoded.Core.Domain.Primitives.MessageFlags;

namespace Mailcoded.Core.Providers;

/// <summary>
/// The IMAP adapter. All MailKit usage in the product is confined to this folder (SPEC invariant 8);
/// everything crossing back out is a Domain type.
/// </summary>
public sealed partial class ImapProvider : IMailProvider
{
    private readonly IClock clock;
    private readonly ImapProviderOptions options;
    private readonly ImapCommandQueue queue = new();
    private readonly Dictionary<string, IMailFolder> folderCache = new(StringComparer.Ordinal);

    private AccountConfig? config;
    private ISecretStore? secrets;
    private ImapConnection? connection;
    private ServerCaps caps = ServerCaps.None;
    private ServerQuirks quirks = ServerQuirks.None;
    private long lastActivityTicks;
    private int activeWatchers;
    private bool faulted;
    private int disposed;

    public ImapProvider(IClock? clock = null, ImapProviderOptions? options = null)
    {
        this.clock = clock ?? SystemClock.Instance;
        this.options = options ?? ImapProviderOptions.Default;
    }

    public ServerCaps Capabilities => caps;

    /// <summary>
    /// A cached socket flag, never a liveness probe: after a network drop MailKit still reports
    /// true until the next write fails, so commands NOOP-probe a connection that has gone quiet.
    /// </summary>
    public bool IsConnected =>
        !faulted && connection?.Client is { IsConnected: true, IsAuthenticated: true };

    public Task ConnectAsync(AccountConfig cfg, ISecretStore secretStore, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(secretStore);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        config = cfg;
        secrets = secretStore;

        return queue.RunAsync(async token =>
        {
            await TearDownAsync().ConfigureAwait(false);

            var session = await ImapConnectionFactory.ConnectAsync(cfg, secretStore, options, token).ConfigureAwait(false);
            connection = session;
            quirks = session.Quirks;
            caps = session.Caps;
            faulted = false;
            lastActivityTicks = clock.Ticks;
        }, ct);
    }

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct) =>
        queue.RunAsync<IReadOnlyList<RemoteFolder>>(token => GuardAsync<IReadOnlyList<RemoteFolder>>("LIST", async inner =>
        {
            var client = await RequireLiveClientAsync(inner).ConfigureAwait(false);
            return await ListFoldersCoreAsync(client, inner).ConfigureAwait(false);
        }, token), ct);

    public Task<ServerFolderInfo> OpenFolderAsync(FolderRef folder, bool writable, CancellationToken ct) =>
        queue.RunAsync<ServerFolderInfo>(token => GuardAsync<ServerFolderInfo>("SELECT", async inner =>
        {
            var client = await RequireLiveClientAsync(inner).ConfigureAwait(false);
            var imapFolder = await ResolveFolderAsync(client, folder.Path, inner).ConfigureAwait(false);
            await OpenAsync(imapFolder, writable, inner).ConfigureAwait(false);
            return Describe(imapFolder, folder.Path);
        }, token), ct);

    public Task<byte[]> FetchRawMessageAsync(FolderRef folder, Uid uid, CancellationToken ct) =>
        queue.RunAsync<byte[]>(token => GuardAsync<byte[]>("UID FETCH BODY[]", async inner =>
        {
            var client = await RequireLiveClientAsync(inner).ConfigureAwait(false);
            var imapFolder = await ResolveFolderAsync(client, folder.Path, inner).ConfigureAwait(false);
            await OpenAsync(imapFolder, false, inner).ConfigureAwait(false);

            using var stream = await imapFolder.GetStreamAsync(new UniqueId(uid.Value), inner).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, inner).ConfigureAwait(false);
            return buffer.ToArray();
        }, token), ct);

    public Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.IsEmpty) return Task.CompletedTask;

        return queue.RunAsync<bool>(token => GuardAsync<bool>("UID STORE", async inner =>
        {
            var client = await RequireLiveClientAsync(inner).ConfigureAwait(false);
            var imapFolder = await ResolveFolderAsync(client, folder.Path, inner).ConfigureAwait(false);
            await OpenAsync(imapFolder, true, inner).ConfigureAwait(false);

            // 23: an EXAMINE-only folder rejects STORE; never send one.
            if (imapFolder.Access != FolderAccess.ReadWrite)
                throw new ProviderException(FailureCategory.Unsupported, $"Folder '{FolderLabel(folder.Path)}' is read-only on the server.");

            // 22: without \* in PERMANENTFLAGS a keyword would not survive the session, so tags
            // stay local instead of being pushed in a loop that never sticks.
            var keywordsPersist = imapFolder.PermanentFlags.HasFlag(ImapFlags.UserDefined);
            IList<string> addKeywords = keywordsPersist ? ImapCapabilityMap.ToStorableKeywords(delta.AddKeywords) : Array.Empty<string>();
            IList<string> removeKeywords = keywordsPersist ? ImapCapabilityMap.ToStorableKeywords(delta.RemoveKeywords) : Array.Empty<string>();

            var (addFlags, removeFlags) = ImapCapabilityMap.ToStoreFlags(delta.Add, delta.Remove);
            var id = new UniqueId(uid.Value);

            if (addFlags != ImapFlags.None || addKeywords.Count > 0)
            {
                await imapFolder.StoreAsync(id, new StoreFlagsRequest(StoreAction.Add, addFlags, addKeywords) { Silent = true }, inner)
                    .ConfigureAwait(false);
            }

            if (removeFlags != ImapFlags.None || removeKeywords.Count > 0)
            {
                await imapFolder.StoreAsync(id, new StoreFlagsRequest(StoreAction.Remove, removeFlags, removeKeywords) { Silent = true }, inner)
                    .ConfigureAwait(false);
            }

            return true;
        }, token), ct);
    }

    public Task<Uid?> MoveAsync(FolderRef from, Uid uid, FolderRef to, CancellationToken ct) =>
        queue.RunAsync<Uid?>(token => GuardAsync<Uid?>("UID MOVE", async inner =>
        {
            var client = await RequireLiveClientAsync(inner).ConfigureAwait(false);
            var source = await ResolveFolderAsync(client, from.Path, inner).ConfigureAwait(false);
            var destination = await ResolveFolderAsync(client, to.Path, inner).ConfigureAwait(false);
            await OpenAsync(source, true, inner).ConfigureAwait(false);

            if (source.Access != FolderAccess.ReadWrite)
                throw new ProviderException(FailureCategory.Unsupported, $"Folder '{FolderLabel(from.Path)}' is read-only on the server.");

            var id = new UniqueId(uid.Value);

            if (caps.Move)
            {
                var moved = await source.MoveToAsync(id, destination, inner).ConfigureAwait(false);
                return moved is { } m && m.Id > 0 ? new Uid(m.Id) : null;
            }

            // 6: Gmail has no real \Deleted. Relabelling is the only safe move there — a COPY plus
            // \Deleted plus EXPUNGE would destroy the message under every other label too.
            if (quirks.HasFlag(ServerQuirks.NoDeletedFlag))
            {
                await RelabelAsync(source, destination, id, inner).ConfigureAwait(false);
                return null;
            }

            var copied = await source.CopyToAsync(id, destination, inner).ConfigureAwait(false);
            await source.StoreAsync(id, new StoreFlagsRequest(StoreAction.Add, ImapFlags.Deleted) { Silent = true }, inner).ConfigureAwait(false);

            // Without UIDPLUS there is no UID EXPUNGE, and a bare EXPUNGE would also remove other
            // messages another client had flagged; leave the copy flagged instead.
            if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                await source.ExpungeAsync(new[] { id }, inner).ConfigureAwait(false);

            return copied is { } c && c.Id > 0 ? new Uid(c.Id) : null;
        }, token), ct);

    public Task<Uid?> AppendAsync(FolderRef folder, byte[] raw, MailcodedFlags flags, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0) throw new ArgumentException("Cannot append an empty message.", nameof(raw));

        return queue.RunAsync<Uid?>(token => GuardAsync<Uid?>("APPEND", async inner =>
        {
            var client = await RequireLiveClientAsync(inner).ConfigureAwait(false);

            if (client.AppendLimit is { } limit && limit > 0 && raw.LongLength > limit)
            {
                throw new ProviderException(
                    FailureCategory.Full,
                    $"Message is {raw.LongLength} bytes; the server APPENDLIMIT is {limit} bytes.");
            }

            var imapFolder = await ResolveFolderAsync(client, folder.Path, inner).ConfigureAwait(false);

            using var source = new MemoryStream(raw, writable: false);
            MimeMessage message;
            try
            {
                message = await MimeMessage.LoadAsync(source, inner).ConfigureAwait(false);
            }
            catch (FormatException ex)
            {
                throw new ProviderException(FailureCategory.Protocol, "The message could not be parsed before APPEND.", ex);
            }

            var request = new AppendRequest(message, ImapCapabilityMap.ToImapFlags(flags), receivedUtc);
            var appended = await imapFolder.AppendAsync(FormatFor(), request, inner).ConfigureAwait(false);
            return appended is { } a && a.Id > 0 ? new Uid(a.Id) : null;
        }, token), ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        await TearDownAsync().ConfigureAwait(false);
        queue.Dispose();
    }

    private async Task<IReadOnlyList<RemoteFolder>> ListFoldersCoreAsync(ImapClient client, CancellationToken ct)
    {
        var result = new List<RemoteFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ns in client.PersonalNamespaces)
        {
            var folders = await ListNamespaceAsync(client, ns, ct).ConfigureAwait(false);
            foreach (var folder in folders)
                AddFolder(result, seen, folder);
        }

        AddFolder(result, seen, client.Inbox);
        return result;
    }

    private static async Task<IList<IMailFolder>> ListNamespaceAsync(ImapClient client, FolderNamespace ns, CancellationToken ct)
    {
        try
        {
            return await client.GetFoldersAsync(ns, StatusItems.Count | StatusItems.Unread, false, ct).ConfigureAwait(false);
        }
        catch (ImapCommandException)
        {
            // Some servers refuse STATUS on parts of the tree; the names still matter more than
            // the badge counts.
            return await client.GetFoldersAsync(ns, StatusItems.None, false, ct).ConfigureAwait(false);
        }
    }

    private static void AddFolder(List<RemoteFolder> result, HashSet<string> seen, IMailFolder folder)
    {
        // 20: a NIL hierarchy delimiter is legal and arrives as '\0'; FolderPath keeps such a
        // flat namespace untouched.
        if (!FolderPath.TryCreate(folder.FullName, folder.DirectorySeparator, out var path)) return;
        if (!seen.Add(path.Value)) return;

        var selectable = !folder.Attributes.HasFlag(FolderAttributes.NoSelect)
            && !folder.Attributes.HasFlag(FolderAttributes.NonExistent);

        result.Add(new RemoteFolder
        {
            Path = path,
            Role = ImapCapabilityMap.ToRole(folder.Attributes, path),
            IsSelectable = selectable,
            TotalCount = folder.Count > 0 ? folder.Count : 0,
            UnreadCount = folder.Unread > 0 ? folder.Unread : 0,
        });
    }

    private async Task<IMailFolder> ResolveFolderAsync(ImapClient client, FolderPath path, CancellationToken ct)
    {
        if (folderCache.TryGetValue(path.Value, out var cached)) return cached;

        IMailFolder resolved;
        if (path.IsInbox)
        {
            resolved = client.Inbox;
        }
        else
        {
            try
            {
                resolved = await client.GetFolderAsync(ToServerPath(path), ct).ConfigureAwait(false);
            }
            catch (FolderNotFoundException ex)
            {
                throw new ProviderException(FailureCategory.NotFound, $"Folder '{FolderLabel(path)}' does not exist on the server.", ex);
            }
        }

        folderCache[path.Value] = resolved;
        return resolved;
    }

    private static async Task OpenAsync(IMailFolder folder, bool writable, CancellationToken ct)
    {
        var desired = writable ? FolderAccess.ReadWrite : FolderAccess.ReadOnly;

        if (folder.IsOpen)
        {
            if (folder.Access == desired) return;
            if (!writable && folder.Access == FolderAccess.ReadWrite) return;
            await folder.CloseAsync(false, ct).ConfigureAwait(false);
        }

        await folder.OpenAsync(desired, ct).ConfigureAwait(false);
    }

    private static ServerFolderInfo Describe(IMailFolder folder, FolderPath path)
    {
        return new ServerFolderInfo
        {
            Path = path,
            UidValidity = new UidValidity(folder.UidValidity),
            HighestModSeq = new ModSeq(folder.HighestModSeq),
            UidNext = folder.UidNext is { } next && next.Id > 0 ? new Uid(next.Id) : null,
            TotalCount = folder.Count > 0 ? folder.Count : 0,
            UnreadCount = folder.Unread > 0 ? folder.Unread : 0,
            Role = ImapCapabilityMap.ToRole(folder.Attributes, path),
            IsReadOnly = folder.Access != FolderAccess.ReadWrite,
            PermanentFlagsAllowCustomKeywords = folder.PermanentFlags.HasFlag(ImapFlags.UserDefined),
        };
    }

    private static async Task RelabelAsync(IMailFolder source, IMailFolder destination, UniqueId id, CancellationToken ct)
    {
        var destinationLabel = GmailLabel(destination);
        var sourceLabel = GmailLabel(source);

        if (!string.Equals(destinationLabel, "\\All", StringComparison.Ordinal))
        {
            await source.StoreAsync(id, new StoreLabelsRequest(StoreAction.Add, new[] { destinationLabel }) { Silent = true }, ct)
                .ConfigureAwait(false);
        }

        await source.StoreAsync(id, new StoreLabelsRequest(StoreAction.Remove, new[] { sourceLabel }) { Silent = true }, ct)
            .ConfigureAwait(false);
    }

    private static string GmailLabel(IMailFolder folder)
    {
        if (folder.Attributes.HasFlag(FolderAttributes.Inbox)) return "\\Inbox";
        if (folder.Attributes.HasFlag(FolderAttributes.All)) return "\\All";
        if (folder.Attributes.HasFlag(FolderAttributes.Sent)) return "\\Sent";
        if (folder.Attributes.HasFlag(FolderAttributes.Drafts)) return "\\Draft";
        if (folder.Attributes.HasFlag(FolderAttributes.Trash)) return "\\Trash";
        if (folder.Attributes.HasFlag(FolderAttributes.Junk)) return "\\Spam";
        if (folder.Attributes.HasFlag(FolderAttributes.Important)) return "\\Important";
        return folder.FullName;
    }

    private FormatOptions FormatFor()
    {
        if (!caps.Utf8Accept) return FormatOptions.Default;

        var format = FormatOptions.Default.Clone();
        format.International = true;
        return format;
    }

    private static string ToServerPath(FolderPath path)
    {
        var delimiter = path.Delimiter;
        return delimiter is '/' or '\0' ? path.Value : path.Value.Replace('/', delimiter);
    }

    private static string FolderLabel(FolderPath path) => ProviderErrors.SanitizeDetail(path.Value, 120);

    private async Task<ImapClient> RequireLiveClientAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var session = connection
            ?? throw new ProviderException(FailureCategory.Network, "The IMAP connection has not been established.");
        var client = session.Client;

        if (faulted || !client.IsConnected || !client.IsAuthenticated)
            throw new ProviderException(FailureCategory.Network, "The IMAP connection is not usable; a reconnect is required.");

        if (clock.Ticks - lastActivityTicks >= options.LivenessProbeIntervalMs)
        {
            try
            {
                await client.NoOpAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                faulted = true;
                throw ProviderErrors.Imap(ex, "liveness probe");
            }

            lastActivityTicks = clock.Ticks;
        }

        return client;
    }

    private async Task<T> GuardAsync<T>(string operation, Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        try
        {
            var result = await body(ct).ConfigureAwait(false);
            lastActivityTicks = clock.Ticks;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkFaulted(ex);
            throw ProviderErrors.Imap(ex, operation);
        }
    }

    private async Task<T> ImapAsync<T>(string operation, Func<Task<T>> call)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            lastActivityTicks = clock.Ticks;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkFaulted(ex);
            throw ProviderErrors.Imap(ex, operation);
        }
    }

    private async Task ImapAsync(string operation, Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
            lastActivityTicks = clock.Ticks;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkFaulted(ex);
            throw ProviderErrors.Imap(ex, operation);
        }
    }

    private void MarkFaulted(Exception ex)
    {
        if (ProviderErrors.CategorizeImap(ex) is FailureCategory.Network or FailureCategory.Auth)
            faulted = true;
    }

    private void LatchQuirk(ServerQuirks quirk)
    {
        if (quirks.HasFlag(quirk)) return;

        quirks |= quirk;
        caps = caps with { Quirks = quirks };
    }

    private async Task TearDownAsync()
    {
        var session = connection;
        connection = null;
        folderCache.Clear();

        if (session is not null)
            await ImapConnectionFactory.CloseAsync(session.Client).ConfigureAwait(false);
    }
}
