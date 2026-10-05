using Mailcoded.Core.Domain.Correspondents;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class CorrespondentStoreTests
{
    private const string Own = "tester@example.com";

    private static readonly DateTimeOffset Day1 = new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(AccountId Account, FolderId Sent, FolderId Inbox)> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct, Own);
        var sent = await StoreSeed.FolderAsync(temp.Store, account, "Sent", FolderRole.Sent, ct);
        var inbox = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        return (account, sent, inbox);
    }

    private static RemoteEnvelope Mail(
        uint uid,
        string to,
        string? cc = null,
        string from = Own,
        DateTimeOffset? date = null,
        string? messageId = null) =>
        StoreSeed.Envelope(uid, from: from, to: to, cc: cc, date: date ?? Day1.AddMinutes(uid), messageId: messageId);

    private static Dictionary<string, Correspondent> Learned(TempStore temp, AccountId account, CancellationToken ct) =>
        temp.Store.ListCorrespondents(account, ct).ToDictionary(c => c.Address, StringComparer.Ordinal);

    [Fact]
    public async Task A_message_ingested_into_Sent_teaches_its_To_and_Cc_with_names_and_dates()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        await StoreSeed.MessageAsync(
            temp.Store, sent,
            Mail(1, "\"Ada Example\" <ada@example.org>, bob@example.net", cc: "Cy <cy@example.org>", date: Day1),
            ct);

        var learned = Learned(temp, account, ct);
        Assert.Equal(3, learned.Count);
        Assert.Equal("Ada Example", learned["ada@example.org"].DisplayName);
        Assert.Null(learned["bob@example.net"].DisplayName);
        Assert.Equal("Cy", learned["cy@example.org"].DisplayName);
        Assert.All(learned.Values, c => Assert.Equal(1, c.Count));
        Assert.All(learned.Values, c => Assert.Equal(Day1, c.LastUsedUtc));
    }

    [Fact]
    public async Task Mail_in_any_other_Folder_teaches_nobody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, _, inbox) = await SeedAsync(temp, ct);
        var archive = await StoreSeed.FolderAsync(temp.Store, account, "Archive", FolderRole.Archive, ct);
        var drafts = await StoreSeed.FolderAsync(temp.Store, account, "Drafts", FolderRole.Drafts, ct);

        await StoreSeed.MessageAsync(temp.Store, inbox, Mail(1, "ada@example.org"), ct);
        await StoreSeed.MessageAsync(temp.Store, inbox, Mail(2, Own, from: "ada@example.org"), ct);
        await StoreSeed.MessageAsync(temp.Store, archive, Mail(3, "ada@example.org"), ct);
        await StoreSeed.MessageAsync(temp.Store, drafts, Mail(4, "ada@example.org"), ct);

        Assert.Empty(Learned(temp, account, ct));
    }

    [Fact]
    public async Task Received_mail_filed_into_Sent_teaches_nobody_because_the_account_did_not_write_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        await StoreSeed.MessageAsync(
            temp.Store, sent,
            Mail(1, $"{Own}, other@example.org", from: "stranger@example.net"),
            ct);
        await StoreSeed.MessageAsync(temp.Store, sent, Mail(2, "ada@example.org", from: "ME-TOO@example.com"), ct);

        Assert.Empty(Learned(temp, account, ct));
    }

    [Fact]
    public async Task Mail_sent_as_a_configured_identity_counts_and_the_identity_itself_never_does()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);
        Assert.True(SenderIdentity.TryCreate("sales@example.com", "Sales", null, out var identity, out var error), error);
        await temp.Store.EditIdentitiesAsync(account, list => SenderIdentities.With(list, identity), ct);

        await StoreSeed.MessageAsync(
            temp.Store, sent,
            Mail(1, "ada@example.org, sales@example.com, TESTER@example.com", from: "Sales <SALES@example.com>"),
            ct);

        Assert.Equal(["ada@example.org"], Learned(temp, account, ct).Keys);
    }

    [Fact]
    public async Task Robot_addresses_and_broadcasts_are_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        await StoreSeed.MessageAsync(temp.Store, sent, Mail(1, "noreply@example.org, mailer-daemon@example.org, ada@example.net"), ct);
        var crowd = string.Join(", ", Enumerable.Range(0, CorrespondentLearning.MaxRecipientsPerMessage + 1).Select(i => $"u{i}@example.org"));
        await StoreSeed.MessageAsync(temp.Store, sent, Mail(2, crowd), ct);

        Assert.Equal(["ada@example.net"], Learned(temp, account, ct).Keys);
    }

    [Fact]
    public async Task Re_ingesting_the_same_Message_counts_it_once_by_any_path()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);
        var envelope = Mail(1, "ada@example.org");

        await temp.Store.IngestEnvelopesAsync(sent, [envelope], ReferencesThreader.Instance, ct);
        await temp.Store.IngestEnvelopesAsync(sent, [envelope], ReferencesThreader.Instance, ct);
        await temp.Store.ApplySyncBatchAsync(new SyncBatch { FolderId = sent, Added = [envelope] }, ReferencesThreader.Instance, ct);
        await using (var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct))
        {
            await bulk.AddAsync(sent, [envelope], ct);
            await bulk.CompleteAsync(ct);
        }

        Assert.Equal(1, Learned(temp, account, ct)["ada@example.org"].Count);
    }

    [Fact]
    public async Task The_sync_batch_and_bulk_paths_learn_new_Messages_too()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        await temp.Store.ApplySyncBatchAsync(
            new SyncBatch { FolderId = sent, Added = [Mail(1, "ada@example.org")] }, ReferencesThreader.Instance, ct);
        await using (var bulk = await temp.Store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct))
        {
            await bulk.AddAsync(sent, [Mail(2, "ada@example.org, bob@example.net")], ct);
            await bulk.CompleteAsync(ct);
        }

        var learned = Learned(temp, account, ct);
        Assert.Equal(2, learned["ada@example.org"].Count);
        Assert.Equal(1, learned["bob@example.net"].Count);
    }

    [Fact]
    public async Task A_second_message_to_the_same_person_adds_a_use_and_the_newest_name_wins_in_any_arrival_order()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        await StoreSeed.MessageAsync(temp.Store, sent, Mail(2, "\"Ada New\" <ada@example.org>", date: Day1.AddDays(2)), ct);
        await StoreSeed.MessageAsync(temp.Store, sent, Mail(1, "\"Ada Old\" <ada@example.org>", date: Day1), ct);
        await StoreSeed.MessageAsync(temp.Store, sent, Mail(3, "Ada@Example.ORG", date: Day1.AddDays(4)), ct);

        var ada = Learned(temp, account, ct)["ada@example.org"];
        Assert.Equal(3, ada.Count);
        Assert.Equal("Ada New", ada.DisplayName);
        Assert.Equal(Day1.AddDays(4), ada.LastUsedUtc);
    }

    [Fact]
    public async Task A_date_in_the_future_is_clamped_to_now()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        await StoreSeed.MessageAsync(temp.Store, sent, Mail(1, "ada@example.org", date: temp.Clock.UtcNow.AddYears(30)), ct);

        Assert.Equal(temp.Clock.UtcNow, Learned(temp, account, ct)["ada@example.org"].LastUsedUtc);
    }

    [Fact]
    public async Task Hostile_header_text_in_Sent_mail_adds_only_whole_valid_addresses()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        await StoreSeed.MessageAsync(
            temp.Store, sent,
            Mail(1, "\"x'); DROP TABLE correspondents;--\"@example.org, <a@b>, ok@example.org, \"unterminated <bad@example.org>"),
            ct);

        Assert.Equal(["ok@example.org"], Learned(temp, account, ct).Keys);
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM correspondents", ct));
    }

    [Fact]
    public async Task One_Accounts_list_is_not_another()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (first, sent, _) = await SeedAsync(temp, ct);
        var second = await StoreSeed.AccountAsync(temp.Store, ct, "other@example.com");

        await StoreSeed.MessageAsync(temp.Store, sent, Mail(1, "ada@example.org"), ct);

        Assert.Single(Learned(temp, first, ct));
        Assert.Empty(Learned(temp, second, ct));
    }

    [Fact]
    public async Task Forgetting_the_Account_forgets_its_correspondents_and_only_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (doomed, sent, _) = await SeedAsync(temp, ct);
        var kept = await StoreSeed.AccountAsync(temp.Store, ct, "kept@example.com");
        await StoreSeed.MessageAsync(temp.Store, sent, Mail(1, "ada@example.org"), ct);
        await temp.Store.RecordCorrespondentsAsync(
            kept, [new NamedAddress(EmailAddress.Parse("bob@example.net"), null)], Day1, ct);

        await temp.Store.ForgetAccountAsync(doomed, ct);

        Assert.Empty(Learned(temp, doomed, ct));
        Assert.Equal(["bob@example.net"], Learned(temp, kept, ct).Keys);
    }

    [Fact]
    public async Task The_migration_creates_both_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        Assert.Equal(
            ["account_id", "address", "display_name", "last_used_utc", "use_count"],
            StoreQuery.ColumnNames(temp.Store, "correspondents", ct));
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM correspondent_backfill", ct));
    }

    private static OutboxRecord Queued(AccountId account, string messageId, string[] to, string[] cc, string[] bcc) => new()
    {
        AccountId = account,
        MessageId = MessageId.Parse(messageId),
        State = OutboxState.Queued,
        Raw = [1, 2, 3],
        Envelope = new OutboxEnvelope
        {
            From = EmailAddress.Parse(Own),
            To = to.Select(EmailAddress.Parse).ToArray(),
            Cc = cc.Select(EmailAddress.Parse).ToArray(),
            Bcc = bcc.Select(EmailAddress.Parse).ToArray(),
        },
    };

    private static async Task<OutboxRecord> EnqueueAsync(TempStore temp, OutboxRecord record, CancellationToken ct)
    {
        var id = await temp.Store.EnqueueOutboxAsync(record, ct);
        return Require.Ref(temp.Store.GetOutbox(id, ct), "the outbox row");
    }

    private static OutboxRecord With(OutboxRecord row, OutboxState state) => new()
    {
        Id = row.Id,
        AccountId = row.AccountId,
        MessageId = row.MessageId,
        State = state,
        Raw = row.Raw,
        Attempts = row.Attempts,
        Envelope = null,
    };

    [Fact]
    public async Task A_send_is_counted_when_the_row_is_marked_sent_and_not_before()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, _, _) = await SeedAsync(temp, ct);
        var row = await EnqueueAsync(temp, Queued(account, "out-1@example.com", ["ada@example.org"], ["cy@example.org"], ["spy@example.net"]), ct);

        await temp.Store.SaveOutboxAsync(With(row, OutboxState.Sending), ct);
        await temp.Store.SaveOutboxAsync(With(row, OutboxState.Failed), ct);
        Assert.Empty(Learned(temp, account, ct));

        await temp.Store.SaveOutboxAsync(With(row, OutboxState.Queued), ct);
        await temp.Store.SaveOutboxAsync(With(row, OutboxState.Sending), ct);
        await temp.Store.SaveOutboxAsync(With(row, OutboxState.Sent), ct);

        var learned = Learned(temp, account, ct);
        Assert.Equal(["ada@example.org", "cy@example.org"], learned.Keys.Order(StringComparer.Ordinal));
        Assert.All(learned.Values, c => Assert.Equal(1, c.Count));
        Assert.All(learned.Values, c => Assert.Equal(temp.Clock.UtcNow, c.LastUsedUtc));
        Assert.DoesNotContain("spy@example.net", learned.Keys);
    }

    [Fact]
    public async Task Saving_a_sent_row_again_never_counts_it_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, _, _) = await SeedAsync(temp, ct);
        var row = await EnqueueAsync(temp, Queued(account, "out-2@example.com", ["ada@example.org"], [], []), ct);

        for (var i = 0; i < 4; i++) await temp.Store.SaveOutboxAsync(With(row, OutboxState.Sent), ct);

        Assert.Equal(1, Learned(temp, account, ct)["ada@example.org"].Count);
    }

    [Fact]
    public async Task Own_robot_and_broadcast_recipients_of_a_send_are_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, _, _) = await SeedAsync(temp, ct);
        var crowd = Enumerable.Range(0, CorrespondentLearning.MaxRecipientsPerMessage + 1).Select(i => $"u{i}@example.org").ToArray();

        var first = await EnqueueAsync(temp, Queued(account, "out-3@example.com", [Own, "noreply@example.org", "ada@example.net"], [], []), ct);
        var second = await EnqueueAsync(temp, Queued(account, "out-4@example.com", crowd, [], []), ct);
        await temp.Store.SaveOutboxAsync(With(first, OutboxState.Sent), ct);
        await temp.Store.SaveOutboxAsync(With(second, OutboxState.Sent), ct);

        Assert.Equal(["ada@example.net"], Learned(temp, account, ct).Keys);
    }

    [Fact]
    public async Task A_row_with_no_stored_envelope_teaches_nobody_and_does_not_fail()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, _, _) = await SeedAsync(temp, ct);
        var bare = Queued(account, "out-5@example.com", [], [], []) with { Envelope = null };
        var row = await EnqueueAsync(temp, bare, ct);

        await temp.Store.SaveOutboxAsync(With(row, OutboxState.Sent), ct);

        Assert.Empty(Learned(temp, account, ct));
    }

    [Fact]
    public async Task The_Sent_copy_of_a_message_mailcoded_sent_is_not_counted_a_second_time_in_either_order()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var (account, sent, _) = await SeedAsync(temp, ct);

        var early = await EnqueueAsync(temp, Queued(account, "out-6@example.com", ["ada@example.org"], [], []), ct);
        await StoreSeed.MessageAsync(temp.Store, sent, Mail(1, "ada@example.org", messageId: "out-6@example.com"), ct);
        await temp.Store.SaveOutboxAsync(With(early, OutboxState.Sent), ct);

        var late = await EnqueueAsync(temp, Queued(account, "out-7@example.com", ["bob@example.net"], [], []), ct);
        await temp.Store.SaveOutboxAsync(With(late, OutboxState.Sent), ct);
        await StoreSeed.MessageAsync(temp.Store, sent, Mail(2, "bob@example.net", messageId: "out-7@example.com"), ct);

        var learned = Learned(temp, account, ct);
        Assert.Equal(1, learned["ada@example.org"].Count);
        Assert.Equal(1, learned["bob@example.net"].Count);
    }

    private static void ForgetTheFeature(TempWorkspace workspace)
    {
        using var connection = new SqliteConnection($"Data Source={workspace.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM correspondents; DELETE FROM correspondent_backfill;";
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Sent_mail_stored_before_the_feature_is_counted_once_when_the_store_next_opens()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        AccountId account;

        using (var store = workspace.OpenStore())
        {
            account = await StoreSeed.AccountAsync(store, ct, Own);
            var sent = await StoreSeed.FolderAsync(store, account, "Sent", FolderRole.Sent, ct);
            var inbox = await StoreSeed.FolderAsync(store, account, "INBOX", FolderRole.Inbox, ct);
            await StoreSeed.MessageAsync(store, sent, Mail(1, "\"Ada\" <ada@example.org>", cc: "cy@example.org", date: Day1), ct);
            await StoreSeed.MessageAsync(store, sent, Mail(2, "ada@example.org", date: Day1.AddDays(2)), ct);
            await StoreSeed.MessageAsync(store, sent, Mail(3, "x@example.org", from: "stranger@example.net"), ct);
            await StoreSeed.MessageAsync(store, inbox, Mail(4, Own, from: "inbound@example.net"), ct);
        }

        ForgetTheFeature(workspace);

        using (var store = workspace.OpenStore())
        {
            var learned = store.ListCorrespondents(account, ct).ToDictionary(c => c.Address);
            Assert.Equal(["ada@example.org", "cy@example.org"], learned.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(2, learned["ada@example.org"].Count);
            Assert.Equal("Ada", learned["ada@example.org"].DisplayName);
            Assert.Equal(Day1.AddDays(2), learned["ada@example.org"].LastUsedUtc);
        }

        using (var store = workspace.OpenStore())
        {
            Assert.Equal(2, store.ListCorrespondents(account, ct).Single(c => c.Address == "ada@example.org").Count);
        }
    }

    [Fact]
    public async Task The_opening_pass_counts_a_sent_outbox_row_with_no_copy_and_skips_one_that_has_a_copy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        AccountId account;

        using (var store = workspace.OpenStore())
        {
            account = await StoreSeed.AccountAsync(store, ct, Own);
            var sent = await StoreSeed.FolderAsync(store, account, "Sent", FolderRole.Sent, ct);

            var lonely = new OutboxRecord
            {
                AccountId = account,
                MessageId = MessageId.Parse("lonely@example.com"),
                State = OutboxState.Queued,
                Raw = [1],
                Envelope = new OutboxEnvelope { From = EmailAddress.Parse(Own), To = [EmailAddress.Parse("lonely@example.org")] },
            };
            var copied = new OutboxRecord
            {
                AccountId = account,
                MessageId = MessageId.Parse("copied@example.com"),
                State = OutboxState.Queued,
                Raw = [1],
                Envelope = new OutboxEnvelope { From = EmailAddress.Parse(Own), To = [EmailAddress.Parse("copied@example.org")] },
            };

            foreach (var record in new[] { lonely, copied })
            {
                var id = await store.EnqueueOutboxAsync(record, ct);
                await store.SaveOutboxAsync(
                    new OutboxRecord { Id = id, AccountId = account, MessageId = record.MessageId, State = OutboxState.Sent, Envelope = null },
                    ct);
            }

            await StoreSeed.MessageAsync(store, sent, Mail(1, "copied@example.org", messageId: "copied@example.com"), ct);
        }

        ForgetTheFeature(workspace);

        using var reopened = workspace.OpenStore();
        var learned = reopened.ListCorrespondents(account, ct).ToDictionary(c => c.Address);
        Assert.Equal(["copied@example.org", "lonely@example.org"], learned.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, learned["copied@example.org"].Count);
        Assert.Equal(1, learned["lonely@example.org"].Count);
    }

    [Fact]
    public async Task The_opening_pass_reads_a_large_Sent_folder_in_batches()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        AccountId account;

        using (var store = workspace.OpenStore())
        {
            account = await StoreSeed.AccountAsync(store, ct, Own);
            var sent = await StoreSeed.FolderAsync(store, account, "Sent", FolderRole.Sent, ct);
            var batch = Enumerable.Range(1, 2500).Select(i => Mail((uint)i, "ada@example.org, u" + (i % 50) + "@example.net")).ToArray();
            await store.IngestEnvelopesAsync(sent, batch, ReferencesThreader.Instance, ct);
        }

        ForgetTheFeature(workspace);

        using var reopened = workspace.OpenStore();
        var learned = reopened.ListCorrespondents(account, ct).ToDictionary(c => c.Address);
        Assert.Equal(2500, learned["ada@example.org"].Count);
        Assert.Equal(50, learned["u7@example.net"].Count);
        Assert.Equal(51, learned.Count);
    }

    [Fact]
    public void A_new_store_has_nothing_to_count_and_records_that_it_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        Assert.Equal(0, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM correspondents", ct));
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM correspondent_backfill", ct));
    }
}
