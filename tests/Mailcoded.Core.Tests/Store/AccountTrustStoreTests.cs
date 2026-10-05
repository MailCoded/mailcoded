using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class AccountTrustStoreTests
{
    private static AccountConfig Config(string email, params string[] trusted) =>
        StoreSeed.AccountConfigFor(email) with { TrustedAuthservIds = trusted };

    [Fact]
    public async Task The_trusted_list_round_trips_through_config_json_and_holds_no_secret()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var id = await temp.Store.AddAccountAsync(Config("a@example.com", "mx.example.net", "*.example.org"), ct);

        var stored = Require.Ref(temp.Store.GetAccount(id, ct), "the account");
        Assert.Equal(["mx.example.net", "*.example.org"], stored.TrustedAuthservIds);

        var none = await temp.Store.AddAccountAsync(Config("b@example.com"), ct);
        Assert.Empty(Require.Ref(temp.Store.GetAccount(none, ct), "the account").TrustedAuthservIds);
    }

    [Fact]
    public async Task Re_adding_an_existing_account_keeps_the_list_it_has()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var id = await temp.Store.AddAccountAsync(Config("a@example.com", "mx.example.net"), ct);
        await temp.Store.AddAccountAsync(Config("a@example.com", "mx.attacker.example.org"), ct);
        Assert.Equal(["mx.example.net"], Require.Ref(temp.Store.GetAccount(id, ct), "the account").TrustedAuthservIds);

        await temp.Store.AddAccountAsync(Config("a@example.com"), ct);
        Assert.Equal(["mx.example.net"], Require.Ref(temp.Store.GetAccount(id, ct), "the account").TrustedAuthservIds);
    }

    [Fact]
    public async Task A_config_update_from_stale_memory_cannot_overwrite_the_list()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var id = await temp.Store.AddAccountAsync(Config("a@example.com"), ct);
        var stale = Require.Ref(temp.Store.GetAccount(id, ct), "the account");

        await temp.Store.SetTrustedAuthservIdsAsync(id, ["mx.example.net"], ct);
        await temp.Store.UpdateAccountAsync(
            stale with { Quirks = new ServerQuirksConfig { Latched = ServerQuirks.None }, TrustedAuthservIds = ["mx.attacker.example.org"] },
            ct);

        Assert.Equal(["mx.example.net"], Require.Ref(temp.Store.GetAccount(id, ct), "the account").TrustedAuthservIds);
    }

    [Fact]
    public async Task Setting_the_list_normalizes_it_and_reports_whether_it_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await temp.Store.AddAccountAsync(Config("a@example.com"), ct);

        Assert.True(await temp.Store.SetTrustedAuthservIdsAsync(id, ["MX.Example.NET.", "bad name", "mx.example.net"], ct));
        Assert.Equal(["mx.example.net"], Require.Ref(temp.Store.GetAccount(id, ct), "the account").TrustedAuthservIds);

        Assert.False(await temp.Store.SetTrustedAuthservIdsAsync(id, ["mx.example.net"], ct));
        Assert.True(await temp.Store.SetTrustedAuthservIdsAsync(id, [], ct));
        Assert.Empty(Require.Ref(temp.Store.GetAccount(id, ct), "the account").TrustedAuthservIds);
    }

    [Fact]
    public async Task Setting_the_list_on_a_missing_account_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var failure = await Assert.ThrowsAsync<StoreException>(
            () => temp.Store.SetTrustedAuthservIdsAsync(new AccountId(999), ["mx.example.net"], ct));

        Assert.Equal(FailureCategory.NotFound, failure.Category);
    }

    [Fact]
    public async Task A_change_clears_recorded_dmarc_results_for_that_account_only()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var first = await temp.Store.AddAccountAsync(Config("a@example.com", "mx.example.net"), ct);
        var second = await temp.Store.AddAccountAsync(Config("b@example.com", "mx.example.net"), ct);
        var firstFolder = await StoreSeed.FolderAsync(temp.Store, first, "INBOX", FolderRole.Inbox, ct);
        var secondFolder = await StoreSeed.FolderAsync(temp.Store, second, "INBOX", FolderRole.Inbox, ct);

        foreach (var folder in new[] { firstFolder, secondFolder })
        {
            await temp.Store.IngestEnvelopesAsync(
                folder,
                [StoreSeed.Envelope(1) with { Dmarc = DmarcResult.Pass }, StoreSeed.Envelope(2) with { Dmarc = DmarcResult.Fail }],
                ReferencesThreader.Instance,
                ct);
        }

        Assert.False(await temp.Store.SetTrustedAuthservIdsAsync(first, ["mx.example.net"], ct));
        Assert.Equal(DmarcResult.Pass, Dmarc(temp, firstFolder, 1, ct));

        Assert.True(await temp.Store.SetTrustedAuthservIdsAsync(first, ["mx.other.example.net"], ct));

        Assert.Null(Dmarc(temp, firstFolder, 1, ct));
        Assert.Null(Dmarc(temp, firstFolder, 2, ct));
        Assert.Equal(DmarcResult.Pass, Dmarc(temp, secondFolder, 1, ct));
        Assert.Equal(DmarcResult.Fail, Dmarc(temp, secondFolder, 2, ct));
    }

    private static DmarcResult? Dmarc(TempStore temp, FolderId folder, uint uid, CancellationToken ct)
    {
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(uid), ct), "the message id");
        return Require.Ref(temp.Store.GetEnvelope(id, ct), "the row").Dmarc;
    }
}
