using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class SyncProgressReportingTests
{
    private static readonly ModSeq Seq = new(100);

    [Fact]
    public async Task A_first_sync_reports_envelopes_up_to_the_server_count_then_closes_them()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, folders) = await SeedAsync(temp, ["INBOX"], ct);
        var provider = new ScriptedMailProvider();
        provider.Folder("INBOX", Info("INBOX", total: 5, uidNext: 6), Arrivals(1, 3, 5));
        var recorder = new ProgressRecorder();

        await Engine(temp).SyncFolderAsync(provider, folders[0], recorder, ct);

        Assert.Equal(
            [(SyncPhase.Envelopes, 3, 5, false), (SyncPhase.Envelopes, 5, 5, false), (SyncPhase.Envelopes, 5, 5, true)],
            recorder.Reports.Select(r => (r.Phase, r.Done, r.Total ?? -1, r.Final)));
        Assert.All(recorder.Reports, r => Assert.Equal(account, r.AccountId));
        Assert.All(recorder.Reports, r => Assert.Equal(folders[0], r.FolderId));
    }

    [Fact]
    public async Task An_account_sync_walks_its_folders_and_closes_the_walk()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folders) = await SeedAsync(temp, ["INBOX", "Archive"], ct);
        var provider = new ScriptedMailProvider();
        provider.Folder("INBOX", Info("INBOX", total: 5, uidNext: 6), Arrivals(1, 3, 5));
        provider.Folder("Archive", Info("Archive", total: 2, uidNext: 3), Arrivals(1, 2, 2));
        var recorder = new ProgressRecorder();

        await Engine(temp).SyncAccountAsync(provider, temp.Store.GetFolder(folders[0], ct)!.AccountId, recorder, ct);

        var reports = recorder.Reports;
        Assert.Equal((SyncPhase.Folders, 0, 2, false), Shape(reports[0]));
        Assert.Equal((SyncPhase.Folders, 2, 2, true), Shape(reports[^1]));

        var walk = reports.Where(r => r.Phase == SyncPhase.Folders).ToList();
        Assert.Equal([0, 1, 2], walk.Select(r => r.Done));
        Assert.All(walk, r => Assert.True(r.FolderId.IsNone));

        var firstWalkStep = reports.IndexOf(walk[1]);
        Assert.All(reports.Take(firstWalkStep).Skip(1), r => Assert.Equal(SyncPhase.Envelopes, r.Phase));
        Assert.Contains(reports.Take(firstWalkStep), r => r.Phase == SyncPhase.Envelopes && r.Final);
    }

    [Fact]
    public async Task An_up_to_date_folder_reports_nothing_of_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folders) = await SeedAsync(temp, ["INBOX"], ct);
        var provider = new ScriptedMailProvider();
        provider.Folder("INBOX", Info("INBOX", total: 5, uidNext: 6), Arrivals(1, 3, 5));
        var engine = Engine(temp);

        await engine.SyncFolderAsync(provider, folders[0], null, ct);
        var recorder = new ProgressRecorder();
        var report = await engine.SyncFolderAsync(provider, folders[0], recorder, ct);

        Assert.Equal("up-to-date", report.FolderReports[0].Plan);
        Assert.Empty(recorder.Reports);
    }

    [Fact]
    public async Task A_folder_that_fails_mid_plan_is_closed_by_the_run_not_the_engine()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folders) = await SeedAsync(temp, ["INBOX"], ct);
        var provider = new ScriptedMailProvider();
        provider.Folder(
            "INBOX",
            Info("INBOX", total: 9, uidNext: 10),
            [.. Envelopes(1, 2), new SyncEvent.BatchComplete(Seq, new Uid(2), 2)],
            failAfter: () => new ProviderException(FailureCategory.Network, "connection dropped"));
        var published = new List<SyncProgress>();
        var run = new SyncProgressThrottle(temp.Clock).Begin(published.Add);

        await Assert.ThrowsAsync<ProviderException>(() => Engine(temp).SyncFolderAsync(provider, folders[0], run, ct));

        Assert.DoesNotContain(published, p => p.Final);

        run.Complete();

        var closing = published[^1];
        Assert.True(closing.Final);
        Assert.Equal((SyncPhase.Envelopes, 2, 9), (closing.Phase, closing.Done, closing.Total ?? -1));
    }

    [Fact]
    public async Task Reported_done_matches_the_folder_total_the_store_keeps()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (_, folders) = await SeedAsync(temp, ["INBOX"], ct);
        var provider = new ScriptedMailProvider();
        provider.Folder("INBOX", Info("INBOX", total: 5, uidNext: 6), Arrivals(1, 3, 5));
        var recorder = new ProgressRecorder();

        await Engine(temp).SyncFolderAsync(provider, folders[0], recorder, ct);

        var final = recorder.Reports[^1];
        Assert.True(final.Final);
        Assert.Equal(temp.Store.GetFolder(folders[0], ct)!.TotalCount, final.Done);
    }

    private static (SyncPhase, int, int, bool) Shape(SyncProgress p) => (p.Phase, p.Done, p.Total ?? -1, p.Final);

    private static SyncEngine Engine(TempStore temp) => new(
        temp.Store,
        ReferencesThreader.Instance,
        temp.Clock,
        new AuditLog(temp.Store, temp.Clock),
        SyncOptions.Default with { ReconcileFolders = false });

    private static ServerFolderInfo Info(string path, int total, uint uidNext) => new()
    {
        Path = FolderPath.Create(path),
        UidValidity = new UidValidity(42),
        HighestModSeq = Seq,
        UidNext = new Uid(uidNext),
        TotalCount = total,
    };

    private static IEnumerable<SyncEvent> Envelopes(uint first, uint last)
    {
        for (var uid = first; uid <= last; uid++) yield return new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(uid, modSeq: 100));
    }

    private static List<SyncEvent> Arrivals(uint first, uint firstBatchEnd, uint last)
    {
        var events = new List<SyncEvent>();
        events.AddRange(Envelopes(first, firstBatchEnd));
        events.Add(new SyncEvent.BatchComplete(Seq, new Uid(firstBatchEnd), (int)(firstBatchEnd - first + 1)));
        events.AddRange(Envelopes(firstBatchEnd + 1, last));
        events.Add(new SyncEvent.BatchComplete(Seq, new Uid(last), (int)(last - firstBatchEnd)));
        events.Add(new SyncEvent.BatchComplete(Seq, new Uid(last), 0));
        return events;
    }

    private static async Task<(AccountId Account, List<FolderId> Folders)> SeedAsync(
        TempStore temp, string[] paths, CancellationToken ct)
    {
        var account = await temp.Store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com"), ct);
        var folders = new List<FolderId>();
        foreach (var path in paths) folders.Add(await StoreSeed.FolderAsync(temp.Store, account, path, FolderRole.None, ct));
        return (account, folders);
    }
}
