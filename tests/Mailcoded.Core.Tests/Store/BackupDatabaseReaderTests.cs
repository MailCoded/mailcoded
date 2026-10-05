using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class BackupDatabaseReaderTests : IDisposable
{
    private readonly TempWorkspace _workspace = TempWorkspace.Create("reader");

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("/tmp/plain/store.db", "file:/tmp/plain/store.db?mode=ro&immutable=1")]
    [InlineData("/tmp/with space/store.db", "file:/tmp/with%20space/store.db?mode=ro&immutable=1")]
    [InlineData("/tmp/q?x#y%z/store.db", "file:/tmp/q%3Fx%23y%25z/store.db?mode=ro&immutable=1")]
    [InlineData("/tmp/a&b=c/store.db", "file:/tmp/a%26b%3Dc/store.db?mode=ro&immutable=1")]
    [InlineData(@"C:\Users\me\x y\store.db", "file:/C:/Users/me/x%20y/store.db?mode=ro&immutable=1")]
    [InlineData("/tmp/caf\u00e9/store.db", "file:/tmp/caf%C3%A9/store.db?mode=ro&immutable=1")]
    public void The_uri_escapes_every_character_sqlite_would_misread(string path, string expected) =>
        Assert.Equal(expected, BackupDatabaseReader.ImmutableUri(path));

    [Fact]
    public void A_path_full_of_uri_metacharacters_still_opens_the_right_file()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(_workspace.Root, "we?ird #dir%20&x=1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "copy.db");
        Make(path, "CREATE TABLE blobs (id INTEGER PRIMARY KEY, sha256 TEXT, ext_path TEXT); INSERT INTO blobs (sha256, ext_path) VALUES ('" + new string('a', 64) + "', '/x');");

        var facts = BackupDatabaseReader.Read(path, true, CancellationToken.None);

        Assert.Equal(1, facts.TableCounts["blobs"]);
        Assert.Equal([new string('a', 64)], facts.ExternalBlobHashes);
        Assert.Equal(["ok"], facts.IntegrityMessages);
    }

    [Fact]
    public void Reading_leaves_no_sidecar_files_and_does_not_change_a_byte()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "PRAGMA journal_mode = WAL; CREATE TABLE t (x); INSERT INTO t VALUES (1);");
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(path);
        var listing = Directory.GetFiles(_workspace.Root).Order().ToList();

        BackupDatabaseReader.Read(path, true, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(listing, Directory.GetFiles(_workspace.Root).Order().ToList());
    }

    [Fact]
    public void A_read_only_file_can_be_inspected()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "CREATE TABLE t (x); INSERT INTO t VALUES (1), (2);");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(2, BackupDatabaseReader.Read(path, false, CancellationToken.None).TableCounts["t"]);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_table_name_that_tries_to_break_out_of_its_quotes_is_counted_safely()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "CREATE TABLE \"x\"\"; DROP TABLE victim; --\" (a); CREATE TABLE victim (a); INSERT INTO victim VALUES (1);");

        var facts = BackupDatabaseReader.Read(path, false, CancellationToken.None);

        Assert.Equal(1, facts.TableCounts["victim"]);
        Assert.Equal(0, facts.TableCounts["x\"; DROP TABLE victim; --"]);
    }

    [Fact]
    public void Virtual_tables_and_their_shadow_tables_are_not_counted()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "CREATE VIRTUAL TABLE fts USING fts5(body); INSERT INTO fts (body) VALUES ('hello'); CREATE TABLE plain (a);");

        var facts = BackupDatabaseReader.Read(path, false, CancellationToken.None);

        Assert.Equal(["plain"], facts.TableCounts.Keys.ToArray());
    }

    [Fact]
    public void A_database_without_a_blobs_table_reports_no_external_blobs()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "CREATE TABLE t (x);");

        Assert.Empty(BackupDatabaseReader.Read(path, false, CancellationToken.None).ExternalBlobHashes);
    }

    [Fact]
    public void Inline_blobs_are_not_external_and_duplicate_hashes_collapse()
    {
        var path = _workspace.PathFor("copy.db");
        var sha = new string('b', 64);
        Make(path, "CREATE TABLE blobs (id INTEGER PRIMARY KEY, sha256 TEXT, bytes BLOB, ext_path TEXT);"
            + "INSERT INTO blobs (sha256, bytes, ext_path) VALUES ('" + new string('a', 64) + "', x'00', NULL), ('" + sha + "', NULL, '/p'), ('" + sha + "', NULL, '/q');");

        Assert.Equal([sha], BackupDatabaseReader.Read(path, false, CancellationToken.None).ExternalBlobHashes);
    }

    [Fact]
    public void The_user_version_is_read()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "CREATE TABLE t (x); PRAGMA user_version = 7;");

        Assert.Equal(7, BackupDatabaseReader.Read(path, false, CancellationToken.None).UserVersion);
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_a_store_exception_not_a_crash()
    {
        var path = _workspace.PathFor("copy.db");
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(new string('x', 5000)));

        var ex = Assert.Throws<StoreException>(() => BackupDatabaseReader.Read(path, true, CancellationToken.None));

        Assert.DoesNotContain(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupted_page_is_found_by_the_integrity_check()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "CREATE TABLE t (x TEXT); CREATE INDEX ix ON t (x);" + string.Join("", Enumerable.Range(0, 400).Select(i => "INSERT INTO t VALUES ('row number " + i + " padding padding padding');")));
        var bytes = File.ReadAllBytes(path);
        for (var i = 4096 * 2; i < 4096 * 2 + 200; i++) bytes[i] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var outcome = Record.Exception(() => BackupDatabaseReader.Read(path, true, CancellationToken.None));

        if (outcome is null)
        {
            var facts = BackupDatabaseReader.Read(path, true, CancellationToken.None);
            Assert.NotEqual(["ok"], facts.IntegrityMessages);
        }
        else
        {
            Assert.IsType<StoreException>(outcome);
        }
    }

    [Fact]
    public void A_hostile_schema_cannot_run_code_during_inspection()
    {
        var path = _workspace.PathFor("copy.db");
        Make(path, "CREATE TABLE t (x); CREATE TRIGGER trg AFTER INSERT ON t BEGIN SELECT load_extension('x'); END; CREATE VIEW v AS SELECT load_extension('y');");

        var facts = BackupDatabaseReader.Read(path, true, CancellationToken.None);

        Assert.Equal(0, facts.TableCounts["t"]);
    }

    [Fact]
    public async Task A_snapshot_taken_by_the_store_is_a_complete_database()
    {
        var ct = TestContext.Current.CancellationToken;
        using var live = TempStore.Create();
        var account = await StoreSeed.AccountAsync(live.Store, ct);
        var folder = await StoreSeed.FolderAsync(live.Store, account, "INBOX", FolderRole.Inbox, ct);
        await StoreSeed.MessageAsync(live.Store, folder, StoreSeed.Envelope(1), ct, bodyText: "hello");
        var target = _workspace.PathFor("snapshot.db");

        await live.Store.BackupToAsync(target, ct);

        var facts = BackupDatabaseReader.Read(target, true, ct);
        Assert.Equal(SqliteStore.LatestSchemaVersion, facts.UserVersion);
        Assert.Equal(1, facts.TableCounts["messages"]);
        Assert.Equal(["ok"], facts.IntegrityMessages);
    }

    [Fact]
    public async Task A_snapshot_refuses_a_destination_that_already_has_content_but_accepts_an_empty_file()
    {
        var ct = TestContext.Current.CancellationToken;
        using var live = TempStore.Create();
        var occupied = _workspace.PathFor("occupied.db");
        File.WriteAllText(occupied, "something");
        var empty = _workspace.PathFor("empty.db");
        File.WriteAllBytes(empty, []);

        await Assert.ThrowsAsync<StoreException>(() => live.Store.BackupToAsync(occupied, ct));
        await live.Store.BackupToAsync(empty, ct);

        Assert.True(new FileInfo(empty).Length > 0);
        Assert.Equal("something", File.ReadAllText(occupied));
    }

    [Fact]
    public async Task A_snapshot_destination_is_a_bound_value_so_quotes_in_it_are_harmless()
    {
        var ct = TestContext.Current.CancellationToken;
        using var live = TempStore.Create();
        var target = _workspace.PathFor("it's; DROP TABLE messages; --.db");

        await live.Store.BackupToAsync(target, ct);

        Assert.True(File.Exists(target));
        Assert.Equal(0, StoreQuery.Scalar(live.Store, "SELECT COUNT(*) FROM messages", ct));
    }

    private static void Make(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
