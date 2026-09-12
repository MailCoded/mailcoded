using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class MigrationTests
{
    private const int CurrentSchemaVersion = 8;

    private const string CjkSubject = "关于下季项目进度的说明";
    private const string CjkBody = "你好，下周的项目进度报告已经完成。请查收。";

    [Fact]
    public void FreshDatabaseReachesCurrentSchemaVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        Assert.Equal(CurrentSchemaVersion, temp.Store.SchemaVersion);
        Assert.Equal(CurrentSchemaVersion, (int)StoreQuery.Scalar(temp.Store, "PRAGMA user_version", ct));
    }

    [Fact]
    public void ReopeningAppliesNoFurtherMigration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();

        string first;
        using (var store = workspace.OpenStore())
        {
            Assert.Equal(CurrentSchemaVersion, store.SchemaVersion);
            first = StoreQuery.SchemaSignature(store, ct);
        }

        using (var store = workspace.OpenStore())
        {
            Assert.Equal(CurrentSchemaVersion, store.SchemaVersion);
            Assert.Equal(CurrentSchemaVersion, (int)StoreQuery.Scalar(store, "PRAGMA user_version", ct));
            Assert.Equal(first, StoreQuery.SchemaSignature(store, ct));
        }
    }

    [Fact]
    public void CompileOptionsIncludeFts5()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var options = StoreQuery.Strings(temp.Store, "PRAGMA compile_options", ct);
        Assert.Contains("ENABLE_FTS5", options);
    }

    [Fact]
    public void MissingFts5FailsStartupWithAnActionableMessage()
    {
        using var workspace = TempWorkspace.Create();

        if (Fts5IsCompiledIn(workspace))
        {
            using var store = workspace.OpenStore();
            var ct = TestContext.Current.CancellationToken;
            var tables = StoreQuery.Strings(store, "SELECT name FROM sqlite_master WHERE type = 'table'", ct);

            // Both indexes are plain CREATE VIRTUAL TABLE statements, so search can never degrade quietly.
            Assert.Contains("msg_fts", tables);
            Assert.Contains("msg_fts_cjk", tables);
            return;
        }

        var failure = Assert.Throws<StoreException>(() =>
        {
            using (workspace.OpenStore()) { }
        });
        Assert.Equal(FailureCategory.Unsupported, failure.Category);
        Assert.Contains("ENABLE_FTS5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("SQLitePCLRaw.bundle_e_sqlite3", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V1CjkIndexRejectsThePhraseQueryTrigramSearchCompilesTo()
    {
        using var workspace = TempWorkspace.Create();
        LegacyV1Database.Create(workspace.DatabasePath, CjkSubject, CjkBody, StoreSeed.BaseDate.ToUnixTimeMilliseconds());

        using var connection = LegacyV1Database.OpenRaw(workspace.DatabasePath);
        Assert.Equal(1L, LegacyV1Database.UserVersion(connection));

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM msg_fts_cjk WHERE msg_fts_cjk MATCH '\"项目进度\"'";

        Assert.Throws<SqliteException>(() => { _ = command.ExecuteScalar(); });
    }

    [Fact]
    public void V1DatabaseUpgradesAndCjkSearchThenWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        LegacyV1Database.Create(workspace.DatabasePath, CjkSubject, CjkBody, StoreSeed.BaseDate.ToUnixTimeMilliseconds());

        using var store = workspace.OpenStore();
        Assert.Equal(CurrentSchemaVersion, store.SchemaVersion);

        var cjkTableSql = Assert.Single(StoreQuery.Strings(
            store,
            "SELECT sql FROM sqlite_master WHERE name = 'msg_fts_cjk'",
            ct));

        Assert.DoesNotContain("detail", cjkTableSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trigram", cjkTableSql, StringComparison.Ordinal);

        var result = store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse("项目进度").Query },
            ct);

        Assert.Equal(SearchRoute.Cjk, result.Route);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(1L, hit.Id.Value);
        Assert.Equal(CjkSubject, hit.Subject);
    }

    [Fact]
    public void UpgradeFromV1AddsTheOutboxAndSyncStateColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create();
        LegacyV1Database.Create(workspace.DatabasePath, CjkSubject, CjkBody, StoreSeed.BaseDate.ToUnixTimeMilliseconds());

        using var store = workspace.OpenStore();

        var outbox = StoreQuery.ColumnNames(store, "outbox", ct);
        Assert.Contains("permanently_failed", outbox);
        Assert.Contains("enhanced_status", outbox);
        Assert.Contains("last_attempt_utc", outbox);
        Assert.Contains("max_attempts", outbox);
        Assert.Contains("envelope_json", outbox);

        Assert.Contains("last_full_diff_utc", StoreQuery.ColumnNames(store, "folder_sync_state", ct));

        var indexes = StoreQuery.Strings(store, "SELECT name FROM sqlite_master WHERE type = 'index'", ct);
        Assert.Contains("ix_outbox_due", indexes);
        Assert.DoesNotContain("ix_msg_folder_uid", indexes);
    }

    private static bool Fts5IsCompiledIn(TempWorkspace workspace)
    {
        using var connection = LegacyV1Database.OpenRaw(workspace.PathFor("fts5-probe.db"));
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA compile_options";

        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(0), "ENABLE_FTS5", StringComparison.Ordinal))
                return true;

        return false;
    }
}
