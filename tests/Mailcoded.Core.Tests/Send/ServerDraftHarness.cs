using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Tests.Support;

namespace Mailcoded.Core.Tests.Send;

internal sealed record AppendCall(FolderRef Folder, byte[] Raw, MessageFlags Flags);

internal sealed record MoveCall(FolderRef From, Uid Uid, UidValidity Expected, FolderRef To);

internal sealed record FlagCall(FolderRef Folder, Uid Uid, FlagDelta Delta);

/// <summary>An IMAP stand-in that records every command; the copy-flag-expunge move fails any test that reaches it.</summary>
internal sealed class FakeServerProvider : IMailProvider
{
    private uint _nextUid = 100;

    public ServerCaps Capabilities { get; set; } = new() { Move = true };

    public bool IsConnected => true;

    public uint FolderValidity { get; set; } = 777;

    public Exception? AppendFailure { get; set; }

    public bool AppendReportsNoUid { get; set; }

    public bool AppendReportsValidity { get; set; }

    public Exception? OpenFailure { get; set; }

    public Exception? MoveFailure { get; set; }

    public Func<Task>? DuringMove { get; set; }

    public AtomicMoveStatus MoveStatus { get; set; } = AtomicMoveStatus.Moved;

    public bool MoveReportsNoNewUid { get; set; }

    public Exception? FlagFailure { get; set; }

    public List<AppendCall> Appends { get; } = [];

    public List<MoveCall> Moves { get; } = [];

    public List<FlagCall> Flags { get; } = [];

    public List<string> Opened { get; } = [];

    public Task<Uid?> AppendAsync(FolderRef folder, byte[] raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        if (AppendFailure is { } failure) throw failure;

        Appends.Add(new AppendCall(folder, [.. raw], flags));
        return Task.FromResult<Uid?>(AppendReportsNoUid ? null : new Uid(_nextUid++));
    }

    public async Task<AppendedMessage> AppendWithValidityAsync(FolderRef folder, byte[] raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        var uid = await AppendAsync(folder, raw, flags, receivedUtc, ct);
        return new AppendedMessage(uid, AppendReportsValidity && uid is not null ? new UidValidity(FolderValidity) : UidValidity.Unknown);
    }

    public Task<Uid?> AppendAsync(FolderRef folder, Stream raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        raw.CopyTo(buffer);
        return AppendAsync(folder, buffer.ToArray(), flags, receivedUtc, ct);
    }

    public Task<ServerFolderInfo> OpenFolderAsync(FolderRef folder, bool writable, CancellationToken ct)
    {
        if (OpenFailure is { } failure) throw failure;

        Opened.Add(folder.Path.Value);
        return Task.FromResult(new ServerFolderInfo { Path = folder.Path, UidValidity = new UidValidity(FolderValidity) });
    }

    public async Task<AtomicMoveResult> MoveAtomicAsync(FolderRef from, Uid uid, UidValidity expected, FolderRef to, CancellationToken ct)
    {
        Moves.Add(new MoveCall(from, uid, expected, to));
        if (MoveFailure is { } failure) throw failure;
        if (DuringMove is { } during) await during();

        return MoveStatus == AtomicMoveStatus.Moved && !MoveReportsNoNewUid
            ? new AtomicMoveResult(AtomicMoveStatus.Moved, new Uid(500))
            : AtomicMoveResult.Of(MoveStatus);
    }

    public Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct)
    {
        if (FlagFailure is { } failure) throw failure;

        Flags.Add(new FlagCall(folder, uid, delta));
        return Task.CompletedTask;
    }

    public Task<Uid?> MoveAsync(FolderRef from, Uid uid, FolderRef to, CancellationToken ct) =>
        throw new InvalidOperationException("The copy, flag and expunge move must never be reached from a send.");

    public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct) => throw Unused();

    public IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderRef folder, SyncPlan plan, CancellationToken ct) => throw Unused();

    public Task<byte[]> FetchRawMessageAsync(FolderRef folder, Uid uid, CancellationToken ct) => throw Unused();

    public Task FetchRawMessageToAsync(FolderRef folder, Uid uid, Stream destination, CancellationToken ct) => throw Unused();

    public Task WatchAsync(FolderRef folder, Func<CancellationToken, Task> onChange, CancellationToken ct) => throw Unused();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static NotSupportedException Unused() => new("Not part of the send path.");
}

internal sealed class ServerDraftHarness : IDisposable
{
    private ServerDraftHarness(SendPathHarness inner, FakeServerProvider provider)
    {
        Inner = inner;
        Provider = provider;
    }

    public SendPathHarness Inner { get; }

    public FakeServerProvider Provider { get; }

    public int Connects { get; private set; }

    public FolderRef Drafts { get; private set; }

    public FolderRef Sent { get; private set; }

    public static async Task<ServerDraftHarness> CreateAsync(
        CancellationToken ct,
        bool optIn = true,
        bool draftsFolder = true,
        bool sentFolder = true,
        params string[] approved)
    {
        var inner = await SendPathHarness.CreateAsync(ct, approved.Length == 0 ? ["@example.org"] : approved);
        var harness = new ServerDraftHarness(inner, new FakeServerProvider());

        if (draftsFolder)
        {
            var id = await StoreSeed.FolderAsync(inner.Store, inner.AccountId, "Drafts", FolderRole.Drafts, ct);
            harness.Drafts = new FolderRef(id, FolderPath.Create("Drafts"));
        }

        if (sentFolder)
        {
            var id = await StoreSeed.FolderAsync(inner.Store, inner.AccountId, "Sent", FolderRole.Sent, ct);
            harness.Sent = new FolderRef(id, FolderPath.Create("Sent"));
        }

        if (optIn) await inner.Store.SetSaveDraftsToServerAsync(inner.AccountId, true, ct);
        return harness;
    }

    public Func<CancellationToken, Task<IMailProvider?>> Connect => _ =>
    {
        Connects++;
        return Task.FromResult<IMailProvider?>(Provider);
    };

    public Task<Mailcoded.Core.Application.SendPreview> PreviewAsync(
        Func<CancellationToken, Task<IMailProvider?>>? connect,
        CancellationToken ct,
        string? bcc = null) =>
        Inner.Send.PreviewAsync(
            SendPathHarness.Caller,
            Inner.AccountId,
            new Mailcoded.Core.Application.DraftRequest
            {
                To = [EmailAddress.Parse("bob@example.org")],
                Bcc = bcc is null ? [] : [EmailAddress.Parse(bcc)],
                Subject = "Quarterly numbers",
                BodyText = "Numbers are in.",
            },
            connect,
            ct);

    public Task<Mailcoded.Core.Application.SendResult> SendAsync(
        Mailcoded.Core.Application.SendPreview preview,
        IMailProvider? provider,
        CancellationToken ct,
        bool appendToSent = true,
        bool serverAutoSaves = false) =>
        Inner.Send.SendAsync(
            SendPathHarness.Caller,
            Inner.Sender,
            provider,
            preview.OutboxId,
            preview.ConfirmToken,
            new Mailcoded.Core.Application.SendOptions { AppendToSent = appendToSent, ServerAutoSavesToSent = serverAutoSaves },
            ct);

    public void Dispose() => Inner.Dispose();
}
