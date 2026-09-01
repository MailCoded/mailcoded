using System.Runtime.CompilerServices;
using MailKit;
using MailKit.Search;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using ImapFlags = MailKit.MessageFlags;

namespace Mailcoded.Core.Providers;

public sealed partial class ImapProvider
{
    public IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderRef folder, SyncPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        return queue.RunStreamAsync(token => ExecutePlanAsync(folder, plan, token), ct);
    }

    private async IAsyncEnumerable<SyncEvent> ExecutePlanAsync(
        FolderRef folder,
        SyncPlan plan,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (plan is SyncPlan.UpToDate) yield break;

        var client = await RequireLiveClientAsync(ct).ConfigureAwait(false);
        var imapFolder = await ImapAsync<IMailFolder>("LIST", () => ResolveFolderAsync(client, folder.Path, ct)).ConfigureAwait(false);

        switch (plan)
        {
            case SyncPlan.Invalidate:
                await foreach (var e in EnumerateAllAsync(imapFolder, ct).ConfigureAwait(false))
                    yield return e;
                break;

            case SyncPlan.QresyncDelta qresync:
                await foreach (var e in QresyncAsync(imapFolder, qresync, ct).ConfigureAwait(false))
                    yield return e;
                break;

            case SyncPlan.CondstoreDelta condstore:
                await foreach (var e in CondstoreAsync(imapFolder, condstore.Since, condstore.FromUid, ct).ConfigureAwait(false))
                    yield return e;
                break;

            case SyncPlan.FullDiff full:
                await foreach (var e in FullDiffAsync(imapFolder, full.KnownUids, ct).ConfigureAwait(false))
                    yield return e;
                break;

            case SyncPlan.Backfill backfill:
                await foreach (var e in BackfillAsync(imapFolder, backfill, ct).ConfigureAwait(false))
                    yield return e;
                break;
        }
    }

    private async IAsyncEnumerable<SyncEvent> EnumerateAllAsync(
        IMailFolder folder,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await OpenReadAsync(folder, ct).ConfigureAwait(false);
        var opened = Snapshot(folder);

        var uids = await ImapAsync<IList<UniqueId>>("UID SEARCH ALL", () => folder.SearchAsync(SearchQuery.All, ct)).ConfigureAwait(false);

        await foreach (var e in FetchEnvelopesAsync(folder, uids, newestFirst: false, ct).ConfigureAwait(false))
            yield return e;

        yield return CompleteCheckpoint(opened);
    }

    private async IAsyncEnumerable<SyncEvent> QresyncAsync(
        IMailFolder folder,
        SyncPlan.QresyncDelta plan,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var capture = new VanishedCapture();
        folder.MessagesVanished += capture.OnVanished;
        try
        {
            string? openFailure = null;
            try
            {
                await OpenQuickResyncAsync(folder, plan, ct).ConfigureAwait(false);
            }
            catch (ProviderException ex)
            {
                openFailure = ex.Message;
            }

            if (openFailure is not null)
            {
                // 9: iCloud advertises QRESYNC and then throws when a folder is opened with it.
                LatchQuirk(ServerQuirks.QresyncBroken);
                yield return new SyncEvent.QuirkDetected(ServerQuirks.QresyncBroken, openFailure);

                await foreach (var e in DegradedFallbackAsync(folder, plan.Since, ct).ConfigureAwait(false))
                    yield return e;
                yield break;
            }

            if (folder.UidValidity != plan.UidValidity.Value)
            {
                // 2: the epoch flipped underneath the session, so nothing that follows belongs to it.
                yield return new SyncEvent.UidValidityChanged(plan.UidValidity, new UidValidity(folder.UidValidity));
                yield break;
            }

            if (BrokenModSeq(folder, plan.Since, out var reason))
            {
                LatchQuirk(ServerQuirks.CondstoreBroken);
                yield return new SyncEvent.QuirkDetected(ServerQuirks.CondstoreBroken, reason);

                await foreach (var e in FullDiffAsync(folder, [], ct).ConfigureAwait(false))
                    yield return e;
                yield break;
            }

            // Captured before the first FETCH: what arrives mid-plan is not covered by this
            // CHANGEDSINCE, and a watermark taken afterwards would silently skip it.
            var opened = Snapshot(folder);

            foreach (var vanished in capture.Vanished)
                yield return new SyncEvent.MessageExpunged(vanished);

            var changed = await ImapAsync<IList<IMessageSummary>>(
                "UID FETCH CHANGEDSINCE",
                () => folder.FetchAsync(UniqueIdRange.All, new FetchRequest(FlagItems()) { ChangedSince = plan.Since.Value }, ct))
                .ConfigureAwait(false);

            // At or below FromUid the message is already stored, so a CHANGEDSINCE hit is a flag
            // change and the FLAGS this fetch already returned are the whole answer.
            var flagOnly = new List<IMessageSummary>();
            var arrivals = new List<UniqueId>(changed.Count);
            foreach (var summary in changed)
            {
                if (summary.UniqueId.Id == 0) continue;
                if (plan.FromUid is { } known && summary.UniqueId.Id <= known.Value) flagOnly.Add(summary);
                else arrivals.Add(summary.UniqueId);
            }

            foreach (var e in FlagChangeEvents(flagOnly))
                yield return e;

            var delivered = new HashSet<uint>();
            await foreach (var e in FetchEnvelopesAsync(folder, arrivals, newestFirst: false, ct).ConfigureAwait(false))
            {
                if (e is SyncEvent.EnvelopeAdded added) delivered.Add(added.Envelope.Uid.Value);
                yield return e;
            }

            var missing = new List<Uid>();
            foreach (var uid in arrivals)
                if (!delivered.Contains(uid.Id)) missing.Add(new Uid(uid.Id));

            if (missing.Count > 0 && !capture.Observed)
            {
                // 4: the server reported changes for messages it then refused to return, and never
                // sent a VANISHED for them, so its expunge bookkeeping cannot be trusted.
                LatchQuirk(ServerQuirks.QresyncBroken);
                yield return new SyncEvent.QuirkDetected(
                    ServerQuirks.QresyncBroken,
                    "Changed UIDs disappeared without a VANISHED response.");
            }

            foreach (var uid in missing)
                yield return new SyncEvent.MessageExpunged(uid);

            yield return CompleteCheckpoint(opened);
        }
        finally
        {
            folder.MessagesVanished -= capture.OnVanished;
        }
    }

    private async IAsyncEnumerable<SyncEvent> CondstoreAsync(
        IMailFolder folder,
        ModSeq since,
        Uid? fromUid,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await OpenReadAsync(folder, ct).ConfigureAwait(false);
        var opened = Snapshot(folder);

        if (BrokenModSeq(folder, since, out var reason))
        {
            LatchQuirk(ServerQuirks.CondstoreBroken);
            yield return new SyncEvent.QuirkDetected(ServerQuirks.CondstoreBroken, reason);

            await foreach (var e in FullDiffAsync(folder, [], ct).ConfigureAwait(false))
                yield return e;
            yield break;
        }

        if (fromUid is { } threshold)
        {
            var changed = await ImapAsync<IList<IMessageSummary>>(
                "UID FETCH CHANGEDSINCE",
                () => folder.FetchAsync(UniqueIdRange.All, new FetchRequest(FlagItems()) { ChangedSince = since.Value }, ct))
                .ConfigureAwait(false);

            // FromUid is the first arrival here, so everything below it is an existing message.
            var flagOnly = new List<IMessageSummary>();
            foreach (var summary in changed)
                if (summary.UniqueId.Id > 0 && summary.UniqueId.Id < threshold.Value) flagOnly.Add(summary);

            foreach (var e in FlagChangeEvents(flagOnly))
                yield return e;
        }

        var arrivals = fromUid is { } start
            ? await ImapAsync<IList<UniqueId>>(
                "UID SEARCH arrivals",
                () => folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(start.Value), UniqueId.MaxValue)), ct))
                .ConfigureAwait(false)
            : await ImapAsync<IList<UniqueId>>("UID SEARCH ALL", () => folder.SearchAsync(SearchQuery.All, ct)).ConfigureAwait(false);

        await foreach (var e in FetchEnvelopesAsync(folder, arrivals, newestFirst: false, ct).ConfigureAwait(false))
            yield return e;

        yield return CompleteCheckpoint(opened);
    }

    private async IAsyncEnumerable<SyncEvent> FullDiffAsync(
        IMailFolder folder,
        IReadOnlyList<Uid> knownUids,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await OpenReadAsync(folder, ct).ConfigureAwait(false);
        var opened = Snapshot(folder);

        var serverUids = await ImapAsync<IList<UniqueId>>("UID SEARCH ALL", () => folder.SearchAsync(SearchQuery.All, ct)).ConfigureAwait(false);

        var serverSet = new HashSet<uint>(serverUids.Count);
        foreach (var uid in serverUids)
            if (uid.Id > 0) serverSet.Add(uid.Id);

        var knownSet = new HashSet<uint>(knownUids.Count);
        foreach (var uid in knownUids)
            knownSet.Add(uid.Value);

        foreach (var uid in knownUids)
            if (!serverSet.Contains(uid.Value)) yield return new SyncEvent.MessageExpunged(uid);

        var arrivals = new List<UniqueId>();
        var existing = new List<UniqueId>();
        foreach (var uid in serverUids)
        {
            if (uid.Id == 0) continue;
            if (knownSet.Contains(uid.Id)) existing.Add(uid);
            else arrivals.Add(uid);
        }

        await foreach (var e in FetchFlagsAsync(folder, existing, ct).ConfigureAwait(false))
            yield return e;

        await foreach (var e in FetchEnvelopesAsync(folder, arrivals, newestFirst: false, ct).ConfigureAwait(false))
            yield return e;

        yield return CompleteCheckpoint(opened);
    }

    private async IAsyncEnumerable<SyncEvent> BackfillAsync(
        IMailFolder folder,
        SyncPlan.Backfill plan,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await OpenReadAsync(folder, ct).ConfigureAwait(false);

        var range = new UniqueIdRange(new UniqueId(plan.FromUid.Value), new UniqueId(plan.ToUid.Value));
        var uids = await ImapAsync<IList<UniqueId>>("UID SEARCH window", () => folder.SearchAsync(SearchQuery.Uids(range), ct)).ConfigureAwait(false);

        // 32: newest-first, so a 500k folder becomes useful from the top down. The caller owns the
        // resumable cursor, so no completion checkpoint is emitted here.
        await foreach (var e in FetchEnvelopesAsync(folder, uids, newestFirst: true, ct).ConfigureAwait(false))
            yield return e;
    }

    private async IAsyncEnumerable<SyncEvent> DegradedFallbackAsync(
        IMailFolder folder,
        ModSeq since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (caps.Condstore && !quirks.HasFlag(ServerQuirks.CondstoreBroken))
        {
            await foreach (var e in CondstoreAsync(folder, since, null, ct).ConfigureAwait(false))
                yield return e;
            yield break;
        }

        await foreach (var e in FullDiffAsync(folder, [], ct).ConfigureAwait(false))
            yield return e;
    }

    private async IAsyncEnumerable<SyncEvent> FetchEnvelopesAsync(
        IMailFolder folder,
        IList<UniqueId> uids,
        bool newestFirst,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (uids.Count == 0) yield break;

        var ordered = new List<UniqueId>(uids.Count);
        foreach (var uid in uids)
            if (uid.Id > 0) ordered.Add(uid);

        ordered.Sort((a, b) => newestFirst ? b.Id.CompareTo(a.Id) : a.Id.CompareTo(b.Id));

        var request = new FetchRequest(EnvelopeItems());
        var batchSize = options.EffectiveBatchSize;

        for (var offset = 0; offset < ordered.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = ordered.GetRange(offset, Math.Min(batchSize, ordered.Count - offset));
            var summaries = await ImapAsync<IList<IMessageSummary>>("UID FETCH", () => folder.FetchAsync(batch, request, ct)).ConfigureAwait(false);

            Uid? highest = null;
            var modSeq = ModSeq.Zero;
            var count = 0;

            foreach (var summary in summaries)
            {
                var envelope = ToRemoteEnvelope(summary);

                // 24: a message expunged between SEARCH and FETCH simply never comes back.
                if (envelope is null) continue;

                count++;
                if (highest is null || envelope.Uid > highest.Value) highest = envelope.Uid;
                modSeq = ModSeq.Max(modSeq, envelope.ModSeq);
                yield return new SyncEvent.EnvelopeAdded(envelope);
            }

            yield return new SyncEvent.BatchComplete(modSeq, highest, count);
        }
    }

    private async IAsyncEnumerable<SyncEvent> FetchFlagsAsync(
        IMailFolder folder,
        List<UniqueId> uids,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (uids.Count == 0) yield break;

        var request = new FetchRequest(FlagItems());
        var batchSize = options.EffectiveBatchSize;

        for (var offset = 0; offset < uids.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = uids.GetRange(offset, Math.Min(batchSize, uids.Count - offset));
            var summaries = await ImapAsync<IList<IMessageSummary>>("UID FETCH FLAGS", () => folder.FetchAsync(batch, request, ct)).ConfigureAwait(false);

            var modSeq = ModSeq.Zero;
            var count = 0;

            foreach (var summary in summaries)
            {
                if (summary.UniqueId.Id == 0) continue;

                var change = ToFlagsChanged(summary);
                modSeq = ModSeq.Max(modSeq, change.ModSeq);
                count++;
                yield return change;
            }

            yield return new SyncEvent.BatchComplete(modSeq, null, count);
        }
    }

    /// <summary>HighestUid stays null: a flag pass proves nothing about which envelopes we hold.</summary>
    private IEnumerable<SyncEvent> FlagChangeEvents(List<IMessageSummary> summaries)
    {
        var batchSize = options.EffectiveBatchSize;
        var pending = 0;
        var modSeq = ModSeq.Zero;

        foreach (var summary in summaries)
        {
            var change = ToFlagsChanged(summary);
            modSeq = ModSeq.Max(modSeq, change.ModSeq);
            pending++;
            yield return change;

            if (pending < batchSize) continue;

            yield return new SyncEvent.BatchComplete(modSeq, null, pending);
            pending = 0;
            modSeq = ModSeq.Zero;
        }

        if (pending > 0)
            yield return new SyncEvent.BatchComplete(modSeq, null, pending);
    }

    private async Task OpenReadAsync(IMailFolder folder, CancellationToken ct) =>
        await ImapAsync("EXAMINE", () => OpenAsync(folder, false, ct)).ConfigureAwait(false);

    private async Task OpenQuickResyncAsync(IMailFolder folder, SyncPlan.QresyncDelta plan, CancellationToken ct)
    {
        var session = connection;
        if (session is null || !session.QuickResyncEnabled)
            throw new ProviderException(FailureCategory.Unsupported, "QRESYNC is not enabled on this connection.");

        if (folder.IsOpen)
            await ImapAsync("CLOSE", () => folder.CloseAsync(false, ct)).ConfigureAwait(false);

        await ImapAsync<FolderAccess>(
            "SELECT QRESYNC",
            () => folder.OpenAsync(FolderAccess.ReadOnly, plan.UidValidity.Value, plan.Since.Value, Array.Empty<UniqueId>(), ct))
            .ConfigureAwait(false);
    }

    private static bool BrokenModSeq(IMailFolder folder, ModSeq since, out string reason)
    {
        if (folder.HighestModSeq == 0)
        {
            reason = "The server reported HIGHESTMODSEQ 0 after advertising CONDSTORE.";
            return true;
        }

        if (!since.IsUnknown && folder.HighestModSeq < since.Value)
        {
            reason = "HIGHESTMODSEQ went backwards, so MODSEQ is not monotonic on this server.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static FolderSnapshot Snapshot(IMailFolder folder) =>
        new(folder.HighestModSeq, folder.UidNext is { } next ? next.Id : 0u);

    private static SyncEvent CompleteCheckpoint(FolderSnapshot opened)
    {
        Uid? highest = opened.UidNext > 1 ? new Uid(opened.UidNext - 1) : null;
        return new SyncEvent.BatchComplete(new ModSeq(opened.HighestModSeq), highest, 0);
    }

    private MessageSummaryItems EnvelopeItems()
    {
        var items = MessageSummaryItems.UniqueId
            | MessageSummaryItems.Flags
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.BodyStructure
            | MessageSummaryItems.Size
            | MessageSummaryItems.InternalDate
            | MessageSummaryItems.References;

        if (caps.Condstore || caps.Qresync) items |= MessageSummaryItems.ModSeq;
        if (caps.GmailExtensions) items |= MessageSummaryItems.GMailMessageId | MessageSummaryItems.GMailThreadId;

        return items;
    }

    private MessageSummaryItems FlagItems()
    {
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Flags;
        if (caps.Condstore || caps.Qresync) items |= MessageSummaryItems.ModSeq;
        return items;
    }

    private static SyncEvent.FlagsChanged ToFlagsChanged(IMessageSummary summary) =>
        new(new Uid(summary.UniqueId.Id),
            ImapCapabilityMap.ToDomainFlags(summary.Flags ?? ImapFlags.None),
            ImapCapabilityMap.ToKeywords(summary.Keywords),
            new ModSeq(summary.ModSeq ?? 0UL));

    private RemoteEnvelope? ToRemoteEnvelope(IMessageSummary summary)
    {
        if (summary.UniqueId.Id == 0) return null;

        var envelope = summary.Envelope;

        return new RemoteEnvelope
        {
            Uid = new Uid(summary.UniqueId.Id),
            Flags = ImapCapabilityMap.ToDomainFlags(summary.Flags ?? ImapFlags.None),
            Keywords = ImapCapabilityMap.ToKeywords(summary.Keywords),
            ModSeq = new ModSeq(summary.ModSeq ?? 0UL),
            MessageIdHeader = Header(envelope?.MessageId, 998),
            References = ToReferences(summary.References),
            InReplyTo = Header(envelope?.InReplyTo, 998),
            Subject = Header(envelope?.Subject, 1_000),
            From = Header(envelope?.From.ToString(), 1_000),
            To = Header(envelope?.To.ToString(), 4_000),
            Cc = Header(envelope?.Cc.ToString(), 4_000),
            DateUtc = ResolveDate(summary),
            Size = summary.Size ?? 0u,
            HasAttachments = HasAttachments(summary),
            GmailMessageId = summary.GMailMessageId,
            GmailThreadId = summary.GMailThreadId,
        };
    }

    private DateTimeOffset ResolveDate(IMessageSummary summary)
    {
        var now = clock.UtcNow;

        // 18: a missing, 1970, or far-future Date header falls back to INTERNALDATE.
        if (summary.Envelope?.Date is { } header && IsPlausible(header, now)) return header.ToUniversalTime();
        if (summary.InternalDate is { } received && IsPlausible(received, now)) return received.ToUniversalTime();

        return now;
    }

    private static bool IsPlausible(DateTimeOffset value, DateTimeOffset now) =>
        value.Year >= 1971 && value <= now.AddDays(2);

    private static IReadOnlyList<string> ToReferences(IList<string>? references)
    {
        if (references is null || references.Count == 0) return [];

        var result = new List<string>(references.Count);
        foreach (var reference in references)
        {
            var cleaned = Header(reference, 998);
            if (cleaned is not null) result.Add(cleaned);
        }

        return result;
    }

    private static bool HasAttachments(IMessageSummary summary)
    {
        if (summary.Body is null) return false;

        using var attachments = summary.Attachments.GetEnumerator();
        return attachments.MoveNext();
    }

    /// <summary>Header text is attacker input; strip control characters and cap the length.</summary>
    private static string? Header(string? raw, int maxLength)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        var cleaned = ProviderErrors.SanitizeDetail(raw, maxLength);
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>What the server reported at SELECT/EXAMINE, before this plan fetched anything.</summary>
    private readonly record struct FolderSnapshot(ulong HighestModSeq, uint UidNext);

    private sealed class VanishedCapture
    {
        private readonly List<Uid> vanished = [];

        public bool Observed { get; private set; }

        public IReadOnlyList<Uid> Vanished => vanished;

        public void OnVanished(object? sender, MessagesVanishedEventArgs e)
        {
            Observed = true;
            foreach (var id in e.UniqueIds)
                if (id.Id > 0) vanished.Add(new Uid(id.Id));
        }
    }
}
