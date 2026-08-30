using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>The imperative shell around <see cref="SyncPlanner"/>: plan, execute, fold, persist.</summary>
public sealed class SyncEngine
{
    private readonly SqliteStore _store;
    private readonly IThreader? _threader;
    private readonly IClock _clock;
    private readonly AuditLog _audit;
    private readonly SyncOptions _options;

    public SyncEngine(SqliteStore store, IThreader? threader, IClock clock, AuditLog audit, SyncOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        _store = store;
        _threader = threader;
        _clock = clock;
        _audit = audit;
        _options = options ?? SyncOptions.Default;
    }

    public SyncOptions Options => _options;

    public async Task<SyncReport> SyncAccountAsync(
        IMailProvider provider,
        AccountId accountId,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var account = RequireAccount(accountId, ct);
        var started = _clock.Ticks;

        if (_options.ReconcileFolders) await ReconcileFoldersAsync(provider, accountId, ct).ConfigureAwait(false);

        var folders = _store.ListFolders(accountId, ct);
        var reports = new List<FolderSyncReport>(folders.Count);
        var added = 0;
        var updated = 0;
        var expunged = 0;
        var batches = 0;
        var quirks = ServerQuirks.None;
        var degraded = false;
        var done = 0;

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new SyncProgress
            {
                Phase = "folder",
                FolderId = folder.Id,
                FolderPath = folder.Path.Value,
                FoldersDone = done,
                FolderCount = folders.Count,
                Added = added,
                Updated = updated,
                Expunged = expunged,
            });

            FolderSyncReport report;
            try
            {
                report = await SyncOneFolderAsync(provider, account, folder, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProviderException ex)
            {
                await _audit.ErrorAsync(
                    AuditEvents.SyncFailed,
                    accountId,
                    CallerContext.Internal,
                    AuditText.Fields(("folder", folder.Path.Value), ("category", ex.Category.ToString()), ("detail", ex.Message)),
                    ct).ConfigureAwait(false);
                throw;
            }

            reports.Add(report);
            added += report.Added;
            updated += report.Updated;
            expunged += report.Expunged;
            batches += report.Batches;
            quirks |= report.LatchedQuirks;
            degraded |= report.Degraded;
            done++;

            account = _store.GetAccount(accountId, ct) ?? account;
        }

        var duration = ElapsedMs(started);

        await _audit.InfoAsync(
            AuditEvents.SyncCompleted,
            accountId,
            CallerContext.Internal,
            AuditText.Fields(
                ("folders", AuditText.Number(done)),
                ("added", AuditText.Number(added)),
                ("updated", AuditText.Number(updated)),
                ("expunged", AuditText.Number(expunged)),
                ("ms", AuditText.Number(duration))),
            ct).ConfigureAwait(false);

        progress?.Report(new SyncProgress
        {
            Phase = "done",
            FoldersDone = done,
            FolderCount = folders.Count,
            Added = added,
            Updated = updated,
            Expunged = expunged,
        });

        return new SyncReport
        {
            Added = added,
            Updated = updated,
            Expunged = expunged,
            Batches = batches,
            Folders = done,
            DurationMs = duration,
            Degraded = degraded,
            LatchedQuirks = quirks,
            FolderReports = reports,
        };
    }

    public async Task<SyncReport> SyncFolderAsync(
        IMailProvider provider,
        FolderId folderId,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var folder = _store.GetFolder(folderId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {folderId.Value}.");
        var account = RequireAccount(folder.AccountId, ct);

        var started = _clock.Ticks;
        progress?.Report(new SyncProgress
        {
            Phase = "folder",
            FolderId = folder.Id,
            FolderPath = folder.Path.Value,
            FolderCount = 1,
        });

        var report = await SyncOneFolderAsync(provider, account, folder, ct).ConfigureAwait(false);

        return new SyncReport
        {
            Added = report.Added,
            Updated = report.Updated,
            Expunged = report.Expunged,
            Batches = report.Batches,
            Folders = 1,
            DurationMs = ElapsedMs(started),
            Degraded = report.Degraded,
            LatchedQuirks = report.LatchedQuirks,
            FolderReports = [report],
        };
    }

    /// <summary>Folds the provider LIST into the folders table and reports what the server dropped.</summary>
    public async Task<FolderReconcileReport> ReconcileFoldersAsync(
        IMailProvider provider,
        AccountId accountId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var remote = await provider.ListFoldersAsync(ct).ConfigureAwait(false);

        var selectable = new List<RemoteFolder>(remote.Count);
        foreach (var folder in remote)
        {
            if (!folder.IsSelectable) continue;
            selectable.Add(folder);
        }

        await _store.UpsertFoldersAsync(accountId, selectable, ct).ConfigureAwait(false);

        var local = _store.ListFolders(accountId, ct);
        var orphaned = new List<string>();

        foreach (var known in local)
        {
            ct.ThrowIfCancellationRequested();

            var stillThere = false;
            foreach (var folder in selectable)
            {
                if (!known.Path.NameEquals(folder.Path)) continue;
                stillThere = true;
                break;
            }

            if (stillThere) continue;
            orphaned.Add(known.Path.Value);
        }

        foreach (var path in orphaned)
        {
            await _audit.WarnAsync(
                AuditEvents.FolderOrphaned,
                accountId,
                CallerContext.Internal,
                AuditText.Fields(("folder", path)),
                ct).ConfigureAwait(false);
        }

        await _audit.InfoAsync(
            AuditEvents.FolderReconciled,
            accountId,
            CallerContext.Internal,
            AuditText.Fields(
                ("listed", AuditText.Number(selectable.Count)),
                ("known", AuditText.Number(local.Count)),
                ("orphaned", AuditText.Number(orphaned.Count))),
            ct).ConfigureAwait(false);

        return new FolderReconcileReport
        {
            Listed = selectable.Count,
            Known = local.Count,
            OrphanedPaths = orphaned,
        };
    }

    private async Task<FolderSyncReport> SyncOneFolderAsync(
        IMailProvider provider,
        AccountConfig account,
        FolderSummary folder,
        CancellationToken ct)
    {
        var folderRef = new FolderRef(folder.Id, folder.Path);
        var added = 0;
        var updated = 0;
        var expunged = 0;
        var batches = 0;
        var degraded = false;
        var latched = ServerQuirks.None;
        var planName = "up-to-date";
        var backfillWindows = 0;

        var replans = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var info = await provider.OpenFolderAsync(folderRef, _options.OpenWritable, ct).ConfigureAwait(false);
            var state = _store.LoadFolderState(folder.Id, includeKnownUids: false, ct)
                ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {folder.Id.Value}.");

            var invalidated = false;
            if (!state.HasEverSynced || state.UidValidity != info.UidValidity)
            {
                var previous = state.UidValidity;
                await _store.InvalidateFolderUidsAsync(folder.Id, info.UidValidity, ct).ConfigureAwait(false);
                state = state with
                {
                    UidValidity = info.UidValidity,
                    HighestModSeq = ModSeq.Zero,
                    HighestKnownUid = null,
                    KnownMessageCount = 0,
                    BackfillCursor = null,
                    KnownUids = Array.Empty<Uid>(),
                };
                invalidated = true;

                if (!previous.IsUnknown)
                {
                    await _audit.WarnAsync(
                        AuditEvents.UidValidityChanged,
                        account.Id,
                        CallerContext.Internal,
                        AuditText.Fields(
                            ("folder", folder.Path.Value),
                            ("was", previous.ToString()),
                            ("now", info.UidValidity.ToString())),
                        ct).ConfigureAwait(false);
                }
            }

            var caps = CapabilitiesFor(provider, account, latched);
            var plan = invalidated ? SyncPlanner.PlanInitial(info) : SyncPlanner.Plan(state, info, caps);

            if (plan is SyncPlan.FullDiff)
            {
                state = _store.LoadFolderState(folder.Id, includeKnownUids: true, ct) ?? state;
                plan = SyncPlanner.Plan(state, info, caps);
            }

            planName = PlanName(plan);

            if (plan is SyncPlan.UpToDate) break;

            if (plan is SyncPlan.FullDiff)
            {
                degraded = true;
                await _audit.DegradedSyncAsync(account.Id, folder.Path, "no usable QRESYNC or CONDSTORE path", ct)
                    .ConfigureAwait(false);
            }

            Uid? plannedCursor = null;
            if (plan is SyncPlan.Backfill backfill)
            {
                plannedCursor = state.BackfillCursor ?? NextCursorAbove(backfill.ToUid);
                if (state.BackfillCursor is null && plannedCursor is { } seed)
                {
                    state = state with { BackfillCursor = seed };
                    await _store.SaveFolderStateAsync(state, ct).ConfigureAwait(false);
                }
            }

            var run = await RunPlanAsync(provider, account, folder, folderRef, state, info, plan, plannedCursor, ct)
                .ConfigureAwait(false);

            added += run.Added;
            updated += run.Updated;
            expunged += run.Expunged;
            batches += run.Batches;

            if (run.Quirks != ServerQuirks.None && (latched | run.Quirks) != latched)
            {
                latched |= run.Quirks;
                degraded = true;
                account = await LatchQuirksAsync(account, run.Quirks, folder.Path, run.QuirkDetail, ct).ConfigureAwait(false);
                if (++replans > _options.MaxReplans) break;
                continue;
            }

            if (run.RequiresReplan)
            {
                if (++replans > _options.MaxReplans) break;
                continue;
            }

            if (plan is SyncPlan.Backfill && run.NextCursor is not null && ++backfillWindows < _options.MaxBackfillWindows)
                continue;

            break;
        }

        return new FolderSyncReport
        {
            FolderId = folder.Id,
            Path = folder.Path.Value,
            Plan = planName,
            Added = added,
            Updated = updated,
            Expunged = expunged,
            Batches = batches,
            Degraded = degraded,
            LatchedQuirks = latched,
        };
    }

    private async Task<PlanRun> RunPlanAsync(
        IMailProvider provider,
        AccountConfig account,
        FolderSummary folder,
        FolderRef folderRef,
        FolderState state,
        ServerFolderInfo info,
        SyncPlan plan,
        Uid? plannedCursor,
        CancellationToken ct)
    {
        var pending = new List<SyncEvent>(256);
        var quirks = ServerQuirks.None;
        var quirkDetail = string.Empty;
        var added = 0;
        var updated = 0;
        var expunged = 0;
        var batches = 0;
        var requiresReplan = false;
        ModSeq? checkpointModSeq = null;

        await foreach (var e in provider.SyncFolderAsync(folderRef, plan, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (e is SyncEvent.QuirkDetected quirk)
            {
                quirks |= quirk.Quirk;
                if (quirkDetail.Length == 0) quirkDetail = quirk.Detail;
            }

            pending.Add(e);

            var checkpoint = e is SyncEvent.BatchComplete;
            if (!checkpoint && pending.Count < _options.MaxEventsPerFlush) continue;

            if (e is SyncEvent.BatchComplete complete) checkpointModSeq = complete.HighestModSeq;

            var flush = await FlushAsync(provider, account, folder, state, info, pending, null, plannedCursor, ct)
                .ConfigureAwait(false);

            pending.Clear();
            batches++;
            added += flush.Counts.Added;
            updated += flush.Counts.Updated;
            expunged += flush.Counts.Expunged;

            if (flush.RequiresReplan)
            {
                requiresReplan = true;
                break;
            }

            state = flush.Next;
        }

        Uid? nextCursor = null;
        if (!requiresReplan)
        {
            if (plan is SyncPlan.Backfill backfill)
                nextCursor = backfill.FromUid.Value > 1 ? backfill.FromUid : null;
            else
                nextCursor = state.BackfillCursor;

            var final = await FlushAsync(provider, account, folder, state, info, pending, checkpointModSeq, nextCursor, ct)
                .ConfigureAwait(false);

            batches++;
            added += final.Counts.Added;
            updated += final.Counts.Updated;
            expunged += final.Counts.Expunged;
            requiresReplan = final.RequiresReplan;
            state = final.Next;
        }

        return new PlanRun(added, updated, expunged, batches, quirks, quirkDetail, requiresReplan, nextCursor);
    }

    private async Task<FlushOutcome> FlushAsync(
        IMailProvider provider,
        AccountConfig account,
        FolderSummary folder,
        FolderState state,
        ServerFolderInfo info,
        List<SyncEvent> events,
        ModSeq? reportedModSeq,
        Uid? backfillCursor,
        CancellationToken ct)
    {
        var response = new ServerResponse
        {
            Events = events.ToArray(),
            ReportedHighestModSeq = reportedModSeq,
            NextBackfillCursor = backfillCursor,
            PermanentFlagsAllowCustomKeywords = info.PermanentFlagsAllowCustomKeywords,
        };

        var result = SyncPlanner.Apply(state, response);

        // The epoch flipped mid-batch: nothing after that point belongs to this mailbox generation,
        // so the events are discarded and the next plan re-reads the stored (old) UIDVALIDITY.
        if (result.RequiresReplan) return new FlushOutcome(state, default, true);

        var added = new List<RemoteEnvelope>();
        var flagChanges = new List<FlagUpdate>();
        var expunged = new List<Uid>();
        var keyworded = new List<KeywordObservation>();

        foreach (var e in events)
        {
            switch (e)
            {
                case SyncEvent.EnvelopeAdded envelope:
                    added.Add(envelope.Envelope);
                    if (envelope.Envelope.Keywords.Count > 0)
                        keyworded.Add(new KeywordObservation(envelope.Envelope.Uid, envelope.Envelope.Keywords, envelope.Envelope.Flags));
                    break;

                case SyncEvent.FlagsChanged flags:
                    flagChanges.Add(new FlagUpdate(flags.Uid, flags.Flags, flags.ModSeq));
                    if (flags.Keywords.Count > 0)
                        keyworded.Add(new KeywordObservation(flags.Uid, flags.Keywords, flags.Flags));
                    break;

                case SyncEvent.MessageExpunged removed:
                    expunged.Add(removed.Uid);
                    break;
            }
        }

        var known = ResolveExisting(folder.Id, keyworded, ct);

        var batch = new SyncBatch
        {
            FolderId = folder.Id,
            Added = added,
            FlagChanges = flagChanges,
            Expunged = expunged,
            NextState = result.Next,
            ServerInfo = reportedModSeq is null ? null : info,
        };

        var counts = await _store.ApplySyncBatchAsync(batch, _threader, ct).ConfigureAwait(false);

        if (keyworded.Count > 0)
        {
            await MergeKeywordsAsync(provider, account, folder, result.Next, info, keyworded, known, ct)
                .ConfigureAwait(false);
        }

        return new FlushOutcome(result.Next, counts, false);
    }

    private Dictionary<uint, bool> ResolveExisting(
        FolderId folderId,
        List<KeywordObservation> observations,
        CancellationToken ct)
    {
        var known = new Dictionary<uint, bool>(observations.Count);
        foreach (var observation in observations)
        {
            if (known.ContainsKey(observation.Uid.Value)) continue;
            known[observation.Uid.Value] = _store.FindMessage(folderId, observation.Uid, ct) is not null;
        }

        return known;
    }

    /// <summary>Server keywords become tags; local wins, so a tag is only ever added by this pass.</summary>
    private async Task MergeKeywordsAsync(
        IMailProvider provider,
        AccountConfig account,
        FolderSummary folder,
        FolderState state,
        ServerFolderInfo info,
        List<KeywordObservation> observations,
        Dictionary<uint, bool> known,
        CancellationToken ct)
    {
        foreach (var observation in observations)
        {
            ct.ThrowIfCancellationRequested();

            var messageId = _store.FindMessage(folder.Id, observation.Uid, ct);
            if (messageId is not { } id) continue;

            var localTagsKnown = known.TryGetValue(observation.Uid.Value, out var existed) && existed;
            IReadOnlyList<Tag> localTags = localTagsKnown ? _store.GetTags(id, ct) : [];

            var merge = TagFlagMap.Merge(new TagMergeInput
            {
                LocalTags = localTags,
                LocalFlags = observation.Flags,
                ServerFlags = observation.Flags,
                ServerKeywords = observation.Keywords,
                ServerAcceptsCustomKeywords = state.ServerAcceptsCustomKeywords,
                LocalTagsKnown = localTagsKnown,
            });

            if (merge.TagsChanged || !localTagsKnown)
                await _store.SetTagsAsync(id, merge.CustomTags, ct).ConfigureAwait(false);

            if (!_options.PushTagDivergence || merge.Push.IsEmpty || info.IsReadOnly) continue;

            try
            {
                await provider.SetFlagsAsync(new FolderRef(folder.Id, folder.Path), observation.Uid, merge.Push, ct)
                    .ConfigureAwait(false);
            }
            catch (ProviderException ex)
            {
                await _audit.WarnAsync(
                    AuditEvents.DegradedSync,
                    account.Id,
                    CallerContext.Internal,
                    AuditText.Fields(("folder", folder.Path.Value), ("keyword-push", ex.Category.ToString())),
                    ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<AccountConfig> LatchQuirksAsync(
        AccountConfig account,
        ServerQuirks quirks,
        FolderPath path,
        string detail,
        CancellationToken ct)
    {
        var merged = account.Quirks.Latched | quirks;
        if (merged == account.Quirks.Latched) return account;

        var updated = account with { Quirks = account.Quirks with { Latched = merged } };
        await _store.UpdateAccountAsync(updated, ct).ConfigureAwait(false);

        await _audit.WarnAsync(
            AuditEvents.QuirkLatched,
            account.Id,
            CallerContext.Internal,
            AuditText.Fields(("folder", path.Value), ("quirk", quirks.ToString()), ("detail", detail)),
            ct).ConfigureAwait(false);

        await _audit.DegradedSyncAsync(account.Id, path, quirks.ToString(), ct).ConfigureAwait(false);

        return updated;
    }

    private AccountConfig RequireAccount(AccountId accountId, CancellationToken ct) =>
        _store.GetAccount(accountId, ct)
        ?? throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

    private static ServerCaps CapabilitiesFor(IMailProvider provider, AccountConfig account, ServerQuirks observed)
    {
        var caps = provider.Capabilities;
        var quirks = caps.Quirks | account.Quirks.Latched | observed;
        return quirks == caps.Quirks ? caps : caps with { Quirks = quirks };
    }

    private static Uid? NextCursorAbove(Uid to) =>
        to.Value < uint.MaxValue ? new Uid(to.Value + 1) : to;

    private long ElapsedMs(long startedTicks)
    {
        var elapsed = _clock.Ticks - startedTicks;
        return elapsed < 0 ? 0 : elapsed;
    }

    private static string PlanName(SyncPlan plan) => plan switch
    {
        SyncPlan.Invalidate => "invalidate",
        SyncPlan.QresyncDelta => "qresync",
        SyncPlan.CondstoreDelta => "condstore",
        SyncPlan.FullDiff => "full-diff",
        SyncPlan.Backfill => "backfill",
        _ => "up-to-date",
    };

    private readonly record struct FlushOutcome(FolderState Next, SyncCounts Counts, bool RequiresReplan);

    private readonly record struct KeywordObservation(Uid Uid, IReadOnlyList<string> Keywords, MessageFlags Flags);

    private readonly record struct PlanRun(
        int Added,
        int Updated,
        int Expunged,
        int Batches,
        ServerQuirks Quirks,
        string QuirkDetail,
        bool RequiresReplan,
        Uid? NextCursor);
}
