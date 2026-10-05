using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class ServerDraftStoreTests
{
    private static OutboxRecord Queued(AccountId account, string messageId) => new()
    {
        AccountId = account,
        MessageId = MessageId.Parse(messageId),
        State = OutboxState.Queued,
        Raw = [1, 2, 3],
    };

    private static ServerDraft Saved(uint uid = 41, uint validity = 777) => new()
    {
        FolderPath = "Drafts",
        UidValidity = new UidValidity(validity),
        Uid = new Uid(uid),
        State = ServerDraftState.Saved,
    };

    private static async Task<(TempStore Temp, AccountId Account, long Outbox)> SeedAsync(CancellationToken ct, string messageId = "out-1@example.com")
    {
        var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var outbox = await temp.Store.EnqueueOutboxAsync(Queued(account, messageId), ct);
        return (temp, account, outbox);
    }

    private static ServerDraft? Stored(TempStore temp, long outbox, CancellationToken ct) =>
        Require.Ref(temp.Store.GetOutbox(outbox, ct), "the outbox row").ServerDraft;

    private static void Execute(TempStore temp, string sql)
    {
        using var connection = LegacyV1Database.OpenRaw(temp.Workspace.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task The_migration_adds_the_five_columns_and_a_new_row_has_no_server_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;

        var columns = StoreQuery.ColumnNames(temp.Store, "outbox", ct);
        foreach (var name in new[]
                 {
                     "server_draft_folder", "server_draft_uidvalidity", "server_draft_uid",
                     "server_draft_state", "server_draft_reason",
                 })
        {
            Assert.Contains(name, columns);
        }

        Assert.Null(Stored(temp, outbox, ct));
    }

    [Fact]
    public async Task A_recorded_server_draft_reads_back_exactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;

        await temp.Store.RecordServerDraftAsync(outbox, Saved(uid: 4_000_000_000, validity: 4_000_000_001), ct);

        var draft = Require.Ref(Stored(temp, outbox, ct), "the server draft");
        Assert.Equal("Drafts", draft.FolderPath);
        Assert.Equal(4_000_000_001u, draft.UidValidity.Value);
        Assert.Equal(4_000_000_000u, draft.Uid!.Value.Value);
        Assert.Equal(ServerDraftState.Saved, draft.State);
        Assert.Equal(ServerDraftReason.None, draft.Reason);
    }

    [Fact]
    public async Task A_server_draft_with_no_known_uid_keeps_its_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;

        await temp.Store.RecordServerDraftAsync(
            outbox,
            new ServerDraft
            {
                FolderPath = "Drafts",
                UidValidity = UidValidity.Unknown,
                Uid = null,
                State = ServerDraftState.Left,
                Reason = ServerDraftReason.UidUnknown,
            },
            ct);

        var draft = Require.Ref(Stored(temp, outbox, ct), "the server draft");
        Assert.Null(draft.Uid);
        Assert.True(draft.UidValidity.IsUnknown);
        Assert.Equal(ServerDraftReason.UidUnknown, draft.Reason);
    }

    [Fact]
    public async Task Recording_twice_keeps_the_first_server_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;

        await temp.Store.RecordServerDraftAsync(outbox, Saved(uid: 1), ct);
        await temp.Store.RecordServerDraftAsync(outbox, Saved(uid: 2), ct);

        Assert.Equal(1u, Stored(temp, outbox, ct)!.Uid!.Value.Value);
    }

    [Fact]
    public async Task A_transition_succeeds_once_and_only_from_the_state_it_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;
        await temp.Store.RecordServerDraftAsync(outbox, Saved(), ct);

        Assert.True(await temp.Store.TransitionServerDraftAsync(outbox, ServerDraftState.Saved, ServerDraftState.Moving, ServerDraftReason.None, ct));
        Assert.False(await temp.Store.TransitionServerDraftAsync(outbox, ServerDraftState.Saved, ServerDraftState.Moving, ServerDraftReason.None, ct));
        Assert.Equal(ServerDraftState.Moving, Stored(temp, outbox, ct)!.State);

        Assert.True(await temp.Store.TransitionServerDraftAsync(outbox, ServerDraftState.Moving, ServerDraftState.Moved, ServerDraftReason.None, ct));
        Assert.False(await temp.Store.TransitionServerDraftAsync(outbox, ServerDraftState.Saved, ServerDraftState.Left, ServerDraftReason.NoMoveCapability, ct));
        Assert.Equal(ServerDraftState.Moved, Stored(temp, outbox, ct)!.State);
    }

    [Fact]
    public async Task Two_racing_movers_cannot_both_win_the_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;
        await temp.Store.RecordServerDraftAsync(outbox, Saved(), ct);

        var attempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => temp.Store.TransitionServerDraftAsync(outbox, ServerDraftState.Saved, ServerDraftState.Moving, ServerDraftReason.None, ct), ct))
            .ToArray();

        var winners = (await Task.WhenAll(attempts)).Count(static won => won);

        Assert.Equal(1, winners);
    }

    [Fact]
    public async Task A_row_with_no_server_draft_cannot_be_transitioned()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;

        Assert.False(await temp.Store.TransitionServerDraftAsync(outbox, ServerDraftState.Saved, ServerDraftState.Moving, ServerDraftReason.None, ct));
        Assert.Null(Stored(temp, outbox, ct));
    }

    [Fact]
    public async Task Saving_the_outbox_state_never_touches_the_server_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, account, outbox) = await SeedAsync(ct);
        using var _ = temp;
        await temp.Store.RecordServerDraftAsync(outbox, Saved(), ct);

        await temp.Store.SaveOutboxAsync(
            new OutboxRecord
            {
                Id = outbox,
                AccountId = account,
                MessageId = MessageId.Parse("out-1@example.com"),
                State = OutboxState.Sending,
            },
            ct);

        var draft = Require.Ref(Stored(temp, outbox, ct), "the server draft");
        Assert.Equal(ServerDraftState.Saved, draft.State);
        Assert.Equal(41u, draft.Uid!.Value.Value);
    }

    [Fact]
    public async Task The_sweep_leaves_only_sent_rows_whose_move_never_finished()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, account, sentSaved) = await SeedAsync(ct, "a@example.com");
        using var _ = temp;
        var sentMoving = await temp.Store.EnqueueOutboxAsync(Queued(account, "b@example.com"), ct);
        var sentMoved = await temp.Store.EnqueueOutboxAsync(Queued(account, "c@example.com"), ct);
        var queuedSaved = await temp.Store.EnqueueOutboxAsync(Queued(account, "d@example.com"), ct);
        var sentNone = await temp.Store.EnqueueOutboxAsync(Queued(account, "e@example.com"), ct);

        foreach (var id in new[] { sentSaved, sentMoving, sentMoved, queuedSaved })
            await temp.Store.RecordServerDraftAsync(id, Saved(), ct);

        await temp.Store.TransitionServerDraftAsync(sentMoving, ServerDraftState.Saved, ServerDraftState.Moving, ServerDraftReason.None, ct);
        await temp.Store.TransitionServerDraftAsync(sentMoved, ServerDraftState.Saved, ServerDraftState.Moved, ServerDraftReason.None, ct);
        Execute(temp, $"UPDATE outbox SET state = 'sent' WHERE id IN ({sentSaved}, {sentMoving}, {sentMoved}, {sentNone})");

        Assert.Equal(2, await temp.Store.LeaveInterruptedServerDraftsAsync(ct));

        Assert.Equal(ServerDraftState.Left, Stored(temp, sentSaved, ct)!.State);
        Assert.Equal(ServerDraftReason.Interrupted, Stored(temp, sentSaved, ct)!.Reason);
        Assert.Equal(ServerDraftState.Left, Stored(temp, sentMoving, ct)!.State);
        Assert.Equal(ServerDraftState.Moved, Stored(temp, sentMoved, ct)!.State);
        Assert.Equal(ServerDraftState.Saved, Stored(temp, queuedSaved, ct)!.State);
        Assert.Null(Stored(temp, sentNone, ct));

        Assert.Equal(0, await temp.Store.LeaveInterruptedServerDraftsAsync(ct));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("SAVED")]
    [InlineData("")]
    [InlineData("saved'; DROP TABLE outbox; --")]
    public async Task A_hand_edited_state_that_is_not_ours_reads_as_no_server_draft(string state)
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;
        await temp.Store.RecordServerDraftAsync(outbox, Saved(), ct);

        using (var connection = LegacyV1Database.OpenRaw(temp.Workspace.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE outbox SET server_draft_state = $state WHERE id = $id";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$id", outbox);
            command.ExecuteNonQuery();
        }

        Assert.Null(Stored(temp, outbox, ct));
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM outbox", ct));
    }

    [Theory]
    [InlineData(-5L, 0u)]
    [InlineData(0L, 0u)]
    [InlineData(99_999_999_999L, 0u)]
    [InlineData(1234L, 1234u)]
    public async Task A_hand_edited_uidvalidity_outside_the_range_reads_as_unknown(long stored, uint expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;
        await temp.Store.RecordServerDraftAsync(outbox, Saved(), ct);
        Execute(temp, $"UPDATE outbox SET server_draft_uidvalidity = {stored} WHERE id = {outbox}");

        Assert.Equal(expected, Stored(temp, outbox, ct)!.UidValidity.Value);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(99_999_999_999L)]
    public async Task A_hand_edited_uid_outside_the_range_reads_as_unknown(long stored)
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;
        await temp.Store.RecordServerDraftAsync(outbox, Saved(), ct);
        Execute(temp, $"UPDATE outbox SET server_draft_uid = {stored} WHERE id = {outbox}");

        Assert.Null(Stored(temp, outbox, ct)!.Uid);
    }

    [Fact]
    public async Task Listing_outbox_rows_carries_the_server_draft_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var (temp, _, outbox) = await SeedAsync(ct);
        using var _ = temp;
        await temp.Store.RecordServerDraftAsync(outbox, Saved(), ct);

        var row = Assert.Single(temp.Store.ListOutbox(null, ct));

        Assert.Equal(ServerDraftState.Saved, row.ServerDraft!.State);
        Assert.Empty(row.Raw);
    }

    [Fact]
    public async Task Saving_drafts_to_the_server_is_off_by_default_and_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        Assert.False(Require.Ref(temp.Store.GetAccount(id, ct), "the account").SaveDraftsToServer);

        Assert.True(await temp.Store.SetSaveDraftsToServerAsync(id, true, ct));
        Assert.True(Require.Ref(temp.Store.GetAccount(id, ct), "the account").SaveDraftsToServer);
        Assert.False(await temp.Store.SetSaveDraftsToServerAsync(id, true, ct));

        Assert.True(await temp.Store.SetSaveDraftsToServerAsync(id, false, ct));
        Assert.False(Require.Ref(temp.Store.GetAccount(id, ct), "the account").SaveDraftsToServer);
        Assert.False(await temp.Store.SetSaveDraftsToServerAsync(id, false, ct));
    }

    [Fact]
    public async Task Re_registering_the_account_keeps_the_setting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);
        await temp.Store.SetSaveDraftsToServerAsync(id, true, ct);

        await temp.Store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com") with { DisplayName = "Renamed" }, ct);
        Assert.True(Require.Ref(temp.Store.GetAccount(id, ct), "the account").SaveDraftsToServer);

        await temp.Store.UpdateAccountAsync(
            Require.Ref(temp.Store.GetAccount(id, ct), "the account") with { SaveDraftsToServer = false, DisplayName = "Again" },
            ct);
        Assert.True(Require.Ref(temp.Store.GetAccount(id, ct), "the account").SaveDraftsToServer);
    }

    [Fact]
    public async Task The_setting_never_leaves_a_credential_in_the_stored_config()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);
        await temp.Store.SetSaveDraftsToServerAsync(id, true, ct);

        var json = Assert.Single(StoreQuery.Strings(temp.Store, "SELECT config_json FROM accounts", ct));

        Assert.Contains("\"saveDraftsToServer\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json.Replace("\"auth\":\"password\"", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"saveDraftsToServer\":\"true\"")]
    [InlineData("\"saveDraftsToServer\":1")]
    [InlineData("\"saveDraftsToServer\":null")]
    [InlineData("\"saveDraftsToServer\":[true]")]
    [InlineData("\"saveDraftsToServer\":false")]
    [InlineData("\"other\":true")]
    public async Task A_stored_value_that_is_not_the_json_true_reads_as_off(string fragment)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        Execute(
            temp,
            $$"""UPDATE accounts SET config_json = '{"version":1,"secretRef":"x","auth":"password","imap":{"host":"imap.example.com"},{{fragment}}}' WHERE id = {{id.Value}}""");

        Assert.False(Require.Ref(temp.Store.GetAccount(id, ct), "the account").SaveDraftsToServer);
    }
}
