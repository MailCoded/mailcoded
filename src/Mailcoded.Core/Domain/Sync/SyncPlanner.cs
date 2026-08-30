using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Sync;

/// <summary>
/// The functional core of the sync engine (ARCHITECTURE §12.4). Zero I/O, zero ambient time,
/// zero logging — every row of the RELIABILITY §14.5 edge-case matrix is a table-driven test
/// against these two methods.
/// </summary>
public static class SyncPlanner
{
    /// <summary>
    /// Threshold above which a first sync becomes a resumable newest-first backfill instead of
    /// one enormous enumeration (edge case 32).
    /// </summary>
    public const int BackfillThreshold = 20_000;

    /// <summary>Newest-first backfill window size, in UIDs.</summary>
    public const int BackfillWindow = 5_000;

    /// <summary>
    /// How stale a CONDSTORE-only folder may get before a full diff is forced. CONDSTORE never
    /// reports expunges, so without this a deletion stays invisible indefinitely.
    /// </summary>
    public static readonly TimeSpan CondstoreFullDiffInterval = TimeSpan.FromHours(24);

    public static SyncPlan Plan(FolderState local, ServerFolderInfo server, ServerCaps caps, DateTimeOffset nowUtc = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(caps);

        // 1 & 2: the epoch changed, or we have never synced. Either way local UIDs mean nothing.
        if (local.UidValidity != server.UidValidity)
            return SyncPlan.Invalidated(server);

        // 32: a backfill was started and not finished; finish it before looking for deltas.
        if (local.BackfillCursor is { } cursor)
            return BackfillFrom(cursor);

        var qresyncUsable = caps.Qresync
            && !caps.Quirks.HasFlag(ServerQuirks.QresyncBroken)
            && !local.HighestModSeq.IsUnknown
            && !server.HighestModSeq.IsUnknown;

        // 4: HIGHESTMODSEQ went backwards — the server's MODSEQ is not monotonic, so no delta path is safe.
        var modSeqWentBackwards = !server.HighestModSeq.IsUnknown
            && !local.HighestModSeq.IsUnknown
            && server.HighestModSeq < local.HighestModSeq;

        if (modSeqWentBackwards)
            return SyncPlan.Full(local.KnownUidsOrEmpty());

        if (qresyncUsable)
        {
            return server.HighestModSeq == local.HighestModSeq && NoNewMessages(local, server)
                ? SyncPlan.NoWork
                : SyncPlan.Qresync(local.HighestModSeq, local.UidValidity, local.HighestKnownUid);
        }

        var condstoreUsable = caps.Condstore
            && !caps.Quirks.HasFlag(ServerQuirks.CondstoreBroken)
            && !local.HighestModSeq.IsUnknown
            && server.HighestModSeq > ModSeq.Zero;

        if (condstoreUsable)
        {
            if (FullDiffIsDue(local, nowUtc))
                return SyncPlan.Full(local.KnownUidsOrEmpty());

            // CONDSTORE reports flag changes but never expunges, so a matching MODSEQ still
            // requires the new-arrival SEARCH; only a matching count lets us skip entirely.
            return server.HighestModSeq == local.HighestModSeq && NoNewMessages(local, server)
                ? SyncPlan.NoWork
                : SyncPlan.Condstore(local.HighestModSeq, NextUidAfter(local.HighestKnownUid));
        }

        // 5: no usable extension. Full UID-range diff; the caller logs degraded_sync.
        return SyncPlan.Full(local.KnownUidsOrEmpty());
    }

    /// <summary>
    /// Decides the plan for a folder that has never been synced, given how big the server says it is.
    /// Split out so the "first sync of a 500k mailbox" path is directly testable.
    /// </summary>
    public static SyncPlan PlanInitial(ServerFolderInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.TotalCount <= BackfillThreshold || server.UidNext is not { } uidNext)
            return SyncPlan.Invalidated(server);

        return BackfillFrom(uidNext);
    }

    private static SyncPlan BackfillFrom(Uid cursor)
    {
        var from = cursor.Value > BackfillWindow ? new Uid(cursor.Value - BackfillWindow) : new Uid(1);
        var to = cursor.Value > 1 ? new Uid(cursor.Value - 1) : new Uid(1);
        return new SyncPlan.Backfill(from, to);
    }

    private static bool FullDiffIsDue(FolderState local, DateTimeOffset nowUtc)
    {
        if (nowUtc == default) return false;
        if (local.LastFullDiffUtc is not { } last) return true;
        return nowUtc - last >= CondstoreFullDiffInterval;
    }

    private static bool NoNewMessages(FolderState local, ServerFolderInfo server)
    {
        if (server.UidNext is not { } uidNext) return false;
        if (local.HighestKnownUid is not { } known) return server.TotalCount == 0;
        return uidNext.Value <= known.Value + 1;
    }

    private static Uid? NextUidAfter(Uid? highest) =>
        highest is { } u && u.Value < uint.MaxValue ? new Uid(u.Value + 1) : highest is null ? new Uid(1) : null;

    /// <summary>
    /// Folds one batch of provider events into the next folder state. Pure: the same
    /// (state, response) pair always yields the same result, which is what makes a mid-batch
    /// crash safe to replay.
    /// </summary>
    public static SyncApplyResult Apply(FolderState state, ServerResponse response)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(response);

        var next = state;
        var quirks = ServerQuirks.None;
        var added = 0;
        var updated = 0;
        var expunged = 0;
        var highestUid = state.HighestKnownUid;
        var highestModSeq = state.HighestModSeq;

        foreach (var e in response.Events)
        {
            switch (e)
            {
                case SyncEvent.UidValidityChanged c:
                    // 2: the epoch flipped underneath us. Everything after this point in the batch
                    // belongs to a different mailbox generation, so stop folding and demand a re-plan.
                    return new SyncApplyResult(
                        state with { UidValidity = c.Current, HighestModSeq = ModSeq.Zero, HighestKnownUid = null, BackfillCursor = null },
                        response.Events,
                        Counts: default,
                        Quirks: quirks,
                        RequiresReplan: true);

                case SyncEvent.EnvelopeAdded a:
                    added++;
                    if (highestUid is null || a.Envelope.Uid > highestUid.Value) highestUid = a.Envelope.Uid;
                    highestModSeq = ModSeq.Max(highestModSeq, a.Envelope.ModSeq);
                    break;

                case SyncEvent.FlagsChanged f:
                    updated++;
                    highestModSeq = ModSeq.Max(highestModSeq, f.ModSeq);
                    break;

                case SyncEvent.MessageExpunged:
                    expunged++;
                    break;

                case SyncEvent.QuirkDetected q:
                    quirks |= q.Quirk;
                    break;

                case SyncEvent.BatchComplete b:
                    highestModSeq = ModSeq.Max(highestModSeq, b.HighestModSeq);
                    if (b.HighestUid is { } hu && (highestUid is null || hu > highestUid.Value)) highestUid = hu;
                    break;
            }
        }

        // 4: the server reported a MODSEQ that did not advance past what we already had while
        // still claiming changes. Latch the quirk so the next Plan() drops to a full diff.
        if (response.ReportedHighestModSeq is { } reported)
        {
            if (!reported.IsUnknown && reported < state.HighestModSeq)
                quirks |= ServerQuirks.CondstoreBroken;
            else
                highestModSeq = ModSeq.Max(highestModSeq, reported);
        }

        var count = state.KnownMessageCount + added - expunged;

        next = state with
        {
            HighestModSeq = highestModSeq,
            HighestKnownUid = highestUid,
            KnownMessageCount = count < 0 ? 0 : count,
            BackfillCursor = response.NextBackfillCursor,
            ServerAcceptsCustomKeywords = response.PermanentFlagsAllowCustomKeywords ?? state.ServerAcceptsCustomKeywords,
        };

        return new SyncApplyResult(next, response.Events, new SyncCounts(added, updated, expunged), quirks, RequiresReplan: false);
    }

    private static IReadOnlyList<Uid> KnownUidsOrEmpty(this FolderState state) => state.KnownUids ?? [];
}

/// <summary>
/// A batch of provider output handed to <see cref="SyncPlanner.Apply"/>. Everything the fold
/// needs arrives in this record, so Apply never reaches for I/O.
/// </summary>
public sealed record ServerResponse
{
    public required IReadOnlyList<SyncEvent> Events { get; init; }

    /// <summary>HIGHESTMODSEQ the server reported for this batch, if it reported one.</summary>
    public ModSeq? ReportedHighestModSeq { get; init; }

    /// <summary>Where a resumable backfill should continue, or null when the folder is complete.</summary>
    public Uid? NextBackfillCursor { get; init; }

    /// <summary>Null when the server said nothing about PERMANENTFLAGS in this batch.</summary>
    public bool? PermanentFlagsAllowCustomKeywords { get; init; }

    public static ServerResponse Of(params SyncEvent[] events) => new() { Events = events };
}

public readonly record struct SyncCounts(int Added, int Updated, int Expunged);

public sealed record SyncApplyResult(
    FolderState Next,
    IReadOnlyList<SyncEvent> Events,
    SyncCounts Counts,
    ServerQuirks Quirks,
    bool RequiresReplan);
