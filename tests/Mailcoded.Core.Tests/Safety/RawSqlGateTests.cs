using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class RawSqlGateTests
{
    private static CallerContext Agent => CallerContext.For(CallerKind.Cli, "xunit");

    [Fact]
    public async Task Query_IsRefusedWhenTheEnvironmentFlagIsOff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SqlHarness.CreateAsync(new AgentPolicyOptions(), ct);

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.Search.ExecuteSqlAsync(Agent, "SELECT id FROM messages", 10, ct));

        Assert.Equal(PolicyDenialReason.RawSqlDisabled, denial.Reason);
        Assert.Contains(AgentPolicyOptions.EnableSqlEnvVar, denial.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_RefusalIsAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SqlHarness.CreateAsync(new AgentPolicyOptions(), ct);

        await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.Search.ExecuteSqlAsync(Agent, "SELECT id FROM messages", 10, ct));

        var row = Assert.Single(
            harness.Store.ReadSyncLog(limit: 100, ct: ct).Items,
            static r => r.Event == AuditEvents.SqlQuery);

        Assert.Equal("cli", row.Interface);
        Assert.Contains("decision=denied", row.Detail, StringComparison.Ordinal);
        Assert.Contains("digest=", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", row.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RawSqlPosture_IsReadFromTheEnvironmentAndDefaultsToDeny()
    {
        using (EnvironmentScope.Set((AgentPolicyOptions.EnableSqlEnvVar, null)))
        {
            Assert.False(AgentPolicyOptions.FromEnvironment().RawSqlEnabled);
        }

        using (EnvironmentScope.Set((AgentPolicyOptions.EnableSqlEnvVar, "1")))
        {
            Assert.True(AgentPolicyOptions.FromEnvironment().RawSqlEnabled);
        }
    }

    [Fact]
    public async Task Query_IsRowCappedAndReportsTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SqlHarness.CreateAsync(
            new AgentPolicyOptions { RawSqlEnabled = true, SqlRowCap = 2 },
            ct);

        var result = await harness.Search.ExecuteSqlAsync(Agent, "SELECT id FROM messages ORDER BY id", 0, ct);

        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Query_NeverExceedsTheHardCapWhateverTheCallerAsksFor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SqlHarness.CreateAsync(
            new AgentPolicyOptions { RawSqlEnabled = true, SqlRowCap = 200, SqlRowHardCap = 3 },
            ct);

        var result = await harness.Search.ExecuteSqlAsync(Agent, "SELECT id FROM messages ORDER BY id", 5000, ct);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("INSERT INTO tags (message_id, tag) VALUES (1, 'x')")]
    [InlineData("UPDATE messages SET subject = 'owned'")]
    [InlineData("DELETE FROM messages")]
    [InlineData("DROP TABLE messages")]
    [InlineData("PRAGMA journal_mode = DELETE")]
    [InlineData("PRAGMA query_only = 0")]
    [InlineData("VACUUM")]
    [InlineData("ATTACH DATABASE '/tmp/evil.db' AS evil")]
    [InlineData("SELECT 1; DELETE FROM messages")]
    [InlineData("  -- a comment\n  DELETE FROM messages")]
    public async Task Query_RejectsAnythingThatIsNotASingleReadOnlyStatement(string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SqlHarness.CreateAsync(
            new AgentPolicyOptions { RawSqlEnabled = true },
            ct);

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.Search.ExecuteSqlAsync(Agent, sql, 10, ct));

        Assert.Equal(PolicyDenialReason.RawSqlNotReadOnly, denial.Reason);
        Assert.Equal("Row 1", harness.Store.GetEnvelope(harness.Messages[0], ct)!.Subject);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("  \n select id from messages")]
    [InlineData("WITH t AS (SELECT 1 AS a) SELECT a FROM t")]
    [InlineData("SELECT 1;")]
    [InlineData("/* note */ SELECT 1")]
    public void SingleReadOnlyStatement_AcceptsOnlySelectAndWith(string sql) =>
        Assert.True(AgentPolicy.IsSingleReadOnlyStatement(sql));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SELECTED FROM messages")]
    [InlineData("WITHOUT ROWID")]
    public void SingleReadOnlyStatement_RejectsEverythingElse(string? sql) =>
        Assert.False(AgentPolicy.IsSingleReadOnlyStatement(sql));

    [Fact]
    public async Task ReaderConnection_IsQueryOnlyEvenIfAWriteReachesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SqlHarness.CreateAsync(
            new AgentPolicyOptions { RawSqlEnabled = true },
            ct);

        var before = harness.Store.GetEnvelope(harness.Messages[0], ct)!.Subject;

        Assert.Throws<StoreException>(
            () => { harness.Store.ExecuteReadOnlyQuery("UPDATE messages SET subject = 'owned'", 10, ct); });

        Assert.Throws<StoreException>(
            () => { harness.Store.ExecuteReadOnlyQuery("DELETE FROM messages", 10, ct); });

        Assert.Equal(before, harness.Store.GetEnvelope(harness.Messages[0], ct)!.Subject);
        Assert.Equal(5, harness.Store.ExecuteReadOnlyQuery("SELECT id FROM messages", 100, ct).Rows.Count);
    }

    private sealed class SqlHarness : IAsyncDisposable
    {
        private readonly SurfaceWorkspace _workspace;

        private SqlHarness(
            SurfaceWorkspace workspace,
            SqliteStore store,
            SearchService search,
            AccountId accountId,
            IReadOnlyList<LocalMessageId> messages)
        {
            _workspace = workspace;
            Store = store;
            Search = search;
            AccountId = accountId;
            Messages = messages;
        }

        public SqliteStore Store { get; }
        public SearchService Search { get; }
        public AccountId AccountId { get; }
        public IReadOnlyList<LocalMessageId> Messages { get; }

        public static async Task<SqlHarness> CreateAsync(AgentPolicyOptions options, CancellationToken ct)
        {
            var workspace = new SurfaceWorkspace("raw-sql");

            try
            {
                var clock = new ManualClock();
                var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = workspace.DatabasePath }, clock);
                var audit = new AuditLog(store, clock);
                var search = new SearchService(store, new AgentPolicy(options, clock), audit);

                var accountId = await StoreSeeder.AddAccountAsync(store, ct).ConfigureAwait(false);
                var folderId = await StoreSeeder.AddInboxAsync(store, accountId, ct).ConfigureAwait(false);

                var messages = new List<LocalMessageId>();
                for (var i = 1; i <= 5; i++)
                {
                    messages.Add(await StoreSeeder.AddMessageAsync(
                        store,
                        folderId,
                        uid: (uint)i,
                        messageId: $"sql-{i}@example.test",
                        subject: $"Row {i}",
                        from: "Ada <ada@example.test>",
                        to: "golden@example.test",
                        dateUtc: new DateTimeOffset(2024, 1, i, 0, 0, 0, TimeSpan.Zero),
                        bodyText: $"Body {i}",
                        size: 100 + i,
                        ct: ct).ConfigureAwait(false));
                }

                return new SqlHarness(workspace, store, search, accountId, messages);
            }
            catch (Exception)
            {
                workspace.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            _workspace.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
