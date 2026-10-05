using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>Turns one Folder's flushes into progress snapshots; the arithmetic lives here so the engine only calls it.</summary>
internal sealed class FolderSyncProgress
{
    private readonly IProgress<SyncProgress>? _sink;
    private readonly AccountId _accountId;
    private readonly FolderSummary _folder;

    private int _held;
    private int _serverCount;
    private int _flagsDone;
    private int? _flagsTotal;
    private bool _envelopesReported;
    private bool _flagsReported;

    public FolderSyncProgress(IProgress<SyncProgress>? sink, AccountId accountId, FolderSummary folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        _sink = sink;
        _accountId = accountId;
        _folder = folder;
    }

    public void Begin(int heldLocally, int serverCount, SyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _held = heldLocally;
        _serverCount = serverCount;
        _flagsTotal = plan is SyncPlan.FullDiff diff ? diff.KnownUids.Count : null;
    }

    public void Flushed(SyncCounts seen, SyncCounts stored)
    {
        _held = Math.Max(0, _held + stored.Added - stored.Expunged);

        if (seen.Added > 0)
        {
            _envelopesReported = true;
            Report(SyncPhase.Envelopes, final: false);
        }

        if (seen.Updated > 0)
        {
            _flagsDone += seen.Updated;
            _flagsReported = true;
            Report(SyncPhase.Flags, final: false);
        }
    }

    public void Finish()
    {
        if (_envelopesReported) Report(SyncPhase.Envelopes, final: true);
        if (_flagsReported) Report(SyncPhase.Flags, final: true);
    }

    private void Report(SyncPhase phase, bool final)
    {
        if (_sink is null) return;

        var (done, total) = phase == SyncPhase.Envelopes
            ? (_held, (int?)Math.Max(_serverCount, _held))
            : (_flagsDone, _flagsTotal is { } t ? Math.Max(t, _flagsDone) : null);

        _sink.Report(new SyncProgress
        {
            AccountId = _accountId,
            Phase = phase,
            FolderId = _folder.Id,
            FolderPath = _folder.Path.Value,
            Done = done,
            Total = total,
            Final = final,
        });
    }
}
