using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Sync;

/// <summary>
/// The decision the pure planner reached about how to bring one folder up to date.
/// Sealed hierarchy with an exhaustive switch; migrate to a discriminated union when C# ships them.
/// </summary>
public abstract record SyncPlan
{
    private SyncPlan() { }

    /// <summary>UIDVALIDITY changed (or first sync): discard local UIDs and re-enumerate. Edge cases 1, 2.</summary>
    public sealed record Invalidate(ServerFolderInfo Server) : SyncPlan;

    /// <summary>QRESYNC fast path: ask for changes since <paramref name="Since"/> and trust VANISHED.</summary>
    public sealed record QresyncDelta(ModSeq Since, UidValidity UidValidity, Uid? FromUid) : SyncPlan;

    /// <summary>CONDSTORE path: CHANGEDSINCE flag fetch plus a UID SEARCH for new arrivals.</summary>
    public sealed record CondstoreDelta(ModSeq Since, Uid? FromUid) : SyncPlan;

    /// <summary>No usable extension: full UID-range diff. Logged as <c>degraded_sync</c>.</summary>
    public sealed record FullDiff(IReadOnlyList<Uid> KnownUids) : SyncPlan;

    /// <summary>Local state already matches the server; nothing to do.</summary>
    public sealed record UpToDate : SyncPlan;

    /// <summary>Resumable newest-first backfill of a very large folder. Edge case 32.</summary>
    public sealed record Backfill(Uid FromUid, Uid ToUid) : SyncPlan;

    public static SyncPlan Invalidated(ServerFolderInfo server) => new Invalidate(server);
    public static SyncPlan Qresync(ModSeq since, UidValidity uidValidity, Uid? fromUid) => new QresyncDelta(since, uidValidity, fromUid);
    public static SyncPlan Condstore(ModSeq since, Uid? fromUid) => new CondstoreDelta(since, fromUid);
    public static SyncPlan Full(IReadOnlyList<Uid> knownUids) => new FullDiff(knownUids);
    public static readonly SyncPlan NoWork = new UpToDate();
}
