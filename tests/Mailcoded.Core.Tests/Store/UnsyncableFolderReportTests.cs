using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class UnsyncableFolderReportTests
{
    [Fact]
    public async Task A_folder_the_provider_could_not_represent_is_logged_and_reported_not_dropped_silently()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await temp.Store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com"), ct);
        var provider = new ScriptedMailProvider { UnsyncableFolders = ["Work/2024", "Work/2025"] };

        var report = await Engine(temp).ReconcileFoldersAsync(provider, account, ct);

        Assert.Equal(["Work/2024", "Work/2025"], report.UnsyncableFolders);
        Assert.Equal(
            1L,
            StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM sync_log WHERE event = 'folder_unsyncable' AND level = 'warn'", ct));
        Assert.Equal(
            1L,
            StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM sync_log WHERE event = 'folder_unsyncable' AND detail LIKE '%count=2%Work/2024%'", ct));
    }

    [Fact]
    public async Task A_sync_carries_the_unsyncable_folders_into_its_report_and_a_clean_list_logs_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await temp.Store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com"), ct);

        var clean = await Engine(temp).SyncAccountAsync(new ScriptedMailProvider(), account, null, ct);
        Assert.Empty(clean.UnsyncableFolders);
        Assert.Equal(0L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM sync_log WHERE event = 'folder_unsyncable'", ct));

        var flagged = await Engine(temp).SyncAccountAsync(new ScriptedMailProvider { UnsyncableFolders = ["A/B"] }, account, null, ct);
        Assert.Equal(["A/B"], flagged.UnsyncableFolders);
    }

    private static SyncEngine Engine(TempStore temp) =>
        new(temp.Store, ReferencesThreader.Instance, temp.Clock, new AuditLog(temp.Store, temp.Clock));
}
