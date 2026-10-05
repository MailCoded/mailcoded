using System.Runtime.CompilerServices;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Tests.Support;

/// <summary>Streams a fixed event script per Folder path; the same script every time it is asked.</summary>
internal sealed class ScriptedMailProvider : IMailProvider
{
    private readonly Dictionary<string, Script> scripts = new(StringComparer.Ordinal);

    public ServerCaps Capabilities { get; } = new() { Qresync = true, Condstore = true, Move = true };

    public bool IsConnected => true;

    public void Folder(
        string path,
        ServerFolderInfo info,
        IReadOnlyList<SyncEvent> events,
        Func<Exception>? failAfter = null) =>
        scripts[path] = new Script(info, events, failAfter);

    public Task<ServerFolderInfo> OpenFolderAsync(FolderRef folder, bool writable, CancellationToken ct) =>
        Task.FromResult(scripts[folder.Path.Value].Info);

    public IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderRef folder, SyncPlan plan, CancellationToken ct) =>
        StreamAsync(scripts[folder.Path.Value], ct);

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct)
    {
        var folders = new List<RemoteFolder>(scripts.Count);
        foreach (var path in scripts.Keys) folders.Add(new RemoteFolder { Path = FolderPath.Create(path) });
        return Task.FromResult<IReadOnlyList<RemoteFolder>>(folders);
    }

    public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<byte[]> FetchRawMessageAsync(FolderRef folder, Uid uid, CancellationToken ct) => throw Unused();

    public Task FetchRawMessageToAsync(FolderRef folder, Uid uid, Stream destination, CancellationToken ct) => throw Unused();

    public Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct) => throw Unused();

    public Task<Uid?> MoveAsync(FolderRef from, Uid uid, FolderRef to, CancellationToken ct) => throw Unused();

    public Task<AtomicMoveResult> MoveAtomicAsync(FolderRef from, Uid uid, UidValidity expected, FolderRef to, CancellationToken ct) => throw Unused();

    public Task<Uid?> AppendAsync(FolderRef folder, byte[] raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

    public Task<Uid?> AppendAsync(FolderRef folder, Stream raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

    public Task WatchAsync(FolderRef folder, Func<CancellationToken, Task> onChange, CancellationToken ct) => throw Unused();

    private static NotSupportedException Unused() => new("Not part of the sync path.");

    private static async IAsyncEnumerable<SyncEvent> StreamAsync(Script script, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in script.Events)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return e;
        }

        if (script.FailAfter is { } fail) throw fail();
    }

    private sealed record Script(ServerFolderInfo Info, IReadOnlyList<SyncEvent> Events, Func<Exception>? FailAfter);
}
