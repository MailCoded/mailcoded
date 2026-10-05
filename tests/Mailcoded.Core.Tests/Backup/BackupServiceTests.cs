using System.Runtime.Versioning;
using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Backup;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Backup;

public sealed class BackupServiceTests : IDisposable
{
    private readonly BackupKit _kit = BackupKit.Create();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task A_backup_with_external_blobs_round_trips_through_verification()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = await _kit.SeedAsync(ct);
        var external = blobs.Where(static b => b.IsExternal).ToList();
        Assert.Equal(3, external.Count);

        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        Assert.Equal(_kit.Dest(), result.Directory);
        Assert.Equal(3, result.Manifest.Blobs.Count);
        Assert.Equal(external.Sum(static b => b.Size), result.Manifest.BlobBytes);
        Assert.Equal(SqliteStore.LatestSchemaVersion, result.Manifest.SchemaVersion);
        Assert.Equal("9.9.9-test", result.Manifest.MailcodedVersion);
        Assert.Equal(_kit.Live.Clock.UtcNow, result.Manifest.CreatedUtc);

        foreach (var blob in external)
        {
            var copy = BackupKit.BlobPath(result.Directory, blob.Sha256);
            Assert.Equal(File.ReadAllBytes(blob.ExternalPath!), File.ReadAllBytes(copy));
        }

        var verification = await _kit.Service.VerifyAsync(BackupKit.Human, result.Directory, ct);
        Assert.True(verification.Ok, string.Join("; ", verification.Problems.Select(static p => p.Kind + " " + p.Message)));
        Assert.Equal("ok", verification.Integrity);
        Assert.Equal(3, verification.BlobFilesChecked);
        Assert.Empty(verification.Problems);
    }

    [Fact]
    public async Task The_manifest_counts_match_the_live_tables_and_list_every_real_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);

        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);
        var counts = result.Manifest.Tables.ToDictionary(static t => t.Name, static t => t.Rows);

        Assert.Equal(4, counts["messages"]);
        Assert.Equal(1, counts["accounts"]);
        Assert.Equal(StoreQuery.Scalar(_kit.Store, "SELECT COUNT(*) FROM blobs", ct), counts["blobs"]);
        Assert.Equal(StoreQuery.Scalar(_kit.Store, "SELECT COUNT(*) FROM tags", ct), counts["tags"]);
        Assert.Equal(1, counts["sync_log"]);
        Assert.DoesNotContain(counts.Keys, static n => n.StartsWith("msg_fts", StringComparison.Ordinal));
        Assert.DoesNotContain(counts.Keys, static n => n.StartsWith("sqlite_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_backup_directory_holds_only_the_database_the_blobs_and_the_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);
        File.WriteAllText(Path.Combine(_kit.Live.Workspace.Root, "secrets.vault"), "not a real vault");
        File.WriteAllText(Path.Combine(_kit.Live.Workspace.Root, "keyring.json"), "{}");

        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        var files = Directory.EnumerateFiles(result.Directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(result.Directory, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        var expected = new List<string> { "manifest.json", "store.db" };
        expected.AddRange(result.Manifest.Blobs.Select(b => BackupManifest.BlobRelativePath(b.Sha256)));
        expected.Sort(StringComparer.Ordinal);

        Assert.Equal(expected, files);
        Assert.DoesNotContain(files, static f => f.Contains("secrets", StringComparison.OrdinalIgnoreCase) || f.Contains("keyring", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_restored_copy_opens_and_reads_its_external_blobs_from_the_new_location()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = await _kit.SeedAsync(ct);
        var external = blobs.First(static b => b.IsExternal);
        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        using var restored = TempWorkspace.Create("restore");
        File.Copy(Path.Combine(result.Directory, "store.db"), restored.DatabasePath);
        CopyTree(Path.Combine(result.Directory, "blobs"), restored.BlobDirectory);

        using var store = restored.OpenStore();
        var found = Require.Ref(store.FindBlob(external.Sha256, ct), "the restored blob");

        Assert.Equal(external.Size, found.Size);
        Assert.Equal(File.ReadAllBytes(external.ExternalPath!), store.ReadBlobBytes(found.Id, ct));
        using var stream = Require.Ref(store.OpenBlob(found.Id, ct), "a stream over the restored blob");
        Assert.Equal(external.Size, stream.Length);
    }

    [Fact]
    public async Task Audit_rows_record_the_start_and_the_end_and_the_copy_carries_the_start()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);

        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        var events = StoreQuery.Strings(_kit.Store, "SELECT event || '|' || interface FROM sync_log ORDER BY id", ct);
        Assert.Equal(["backup_started|rpc", "backup_completed|rpc"], events);

        var details = StoreQuery.Strings(_kit.Store, "SELECT detail FROM sync_log WHERE event = 'backup_completed'", ct);
        Assert.Contains("db_bytes=" + result.Manifest.DatabaseBytes, details[0], StringComparison.Ordinal);
        Assert.Contains("blobs=3", details[0], StringComparison.Ordinal);

        var inCopy = BackupDatabaseReader.Read(Path.Combine(result.Directory, "store.db"), false, ct);
        Assert.Equal(1, inCopy.TableCounts["sync_log"]);
    }

    [Fact]
    public async Task Verification_writes_an_audit_row_for_success_and_for_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);
        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        await _kit.Service.VerifyAsync(BackupKit.Human, result.Directory, ct);
        BackupKit.FlipByte(Path.Combine(result.Directory, "store.db"), 200);
        await _kit.Service.VerifyAsync(BackupKit.Human, result.Directory, ct);

        var rows = StoreQuery.Strings(_kit.Store, "SELECT event || '|' || level FROM sync_log WHERE event LIKE 'backup_verif%' ORDER BY id", ct);
        Assert.Equal(["backup_verified|info", "backup_verify_failed|warn"], rows);
    }

    [Fact]
    public async Task A_backup_taken_while_the_store_is_written_verifies_and_ignores_later_blobs()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);
        var account = _kit.Store.ListAccounts(ct)[0].Id;
        var folder = _kit.Store.ListFolders(account, ct)[0].Id;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = Task.Run(async () =>
        {
            uint uid = 100;
            while (!stop.IsCancellationRequested)
            {
                await StoreSeed.MessageAsync(_kit.Store, folder, StoreSeed.Envelope(uid++), CancellationToken.None, bodyText: "concurrent");
                await _kit.Store.StoreBlobAsync(BackupKit.Payload(200 + (int)(uid % 50), (int)uid), CancellationToken.None);
            }
        }, CancellationToken.None);

        BackupResult result;
        try
        {
            await Task.Delay(50, ct);
            result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);
        }
        finally
        {
            await stop.CancelAsync();
            await writer;
        }

        var late = await _kit.Store.StoreBlobAsync(BackupKit.Payload(5000, 12345), ct);
        Assert.DoesNotContain(result.Manifest.Blobs, b => b.Sha256 == late.Sha256);

        var verification = await _kit.Service.VerifyAsync(BackupKit.Human, result.Directory, ct);
        Assert.True(verification.Ok, string.Join("; ", verification.Problems.Select(static p => p.Kind + " " + p.Subject)));
    }

    [Fact]
    public async Task A_second_backup_into_the_same_folder_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);
        await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct));
        Assert.Contains("not empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_attachment_file_fails_the_backup_and_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = await _kit.SeedAsync(ct);
        File.Delete(blobs.First(static b => b.IsExternal).ExternalPath!);

        var ex = await Assert.ThrowsAsync<StoreException>(() => _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct));

        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_kit.Dest()));
        Assert.Contains(
            StoreQuery.Strings(_kit.Store, "SELECT event FROM sync_log", ct),
            static e => e == "backup_failed");
    }

    [Fact]
    public async Task A_damaged_attachment_file_fails_the_backup_instead_of_copying_the_damage()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = await _kit.SeedAsync(ct);
        BackupKit.FlipByte(blobs.First(static b => b.IsExternal).ExternalPath!, 10);

        var ex = await Assert.ThrowsAsync<StoreException>(() => _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct));

        Assert.Contains("does not match its recorded hash", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_kit.Dest()));
    }

    [Fact]
    public async Task A_failed_backup_into_an_existing_empty_folder_empties_it_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = await _kit.SeedAsync(ct);
        Directory.CreateDirectory(_kit.Dest());
        File.Delete(blobs.First(static b => b.IsExternal).ExternalPath!);

        await Assert.ThrowsAsync<StoreException>(() => _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct));

        Assert.True(Directory.Exists(_kit.Dest()));
        Assert.Empty(Directory.GetFileSystemEntries(_kit.Dest()));
    }

    [Fact]
    public async Task A_backup_of_an_empty_store_verifies()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        Assert.Empty(result.Manifest.Blobs);
        Assert.True((await _kit.Service.VerifyAsync(BackupKit.Human, result.Directory, ct)).Ok);
    }

    [Fact]
    public async Task Two_backups_at_once_in_one_process_are_refused_rather_than_interleaved()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct, externalBlobs: 1);

        var gate = new TaskCompletionSource();
        var first = Task.Run(async () =>
        {
            await gate.Task;
            return await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest("one"), ct);
        }, ct);

        var second = Task.Run(async () =>
        {
            await gate.Task;
            return await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest("two"), ct);
        }, ct);

        gate.SetResult();
        var outcomes = await Task.WhenAll(Settle(first), Settle(second));

        Assert.True(outcomes.Count(static o => o is null) >= 1, "at least one backup must succeed");
        foreach (var failure in outcomes.OfType<Exception>())
            Assert.IsType<StoreException>(failure);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_unix_backup_is_private_to_its_owner()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no Unix permission bits.");

        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);
        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(result.Directory));
        foreach (var file in Directory.EnumerateFiles(result.Directory, "*", SearchOption.AllDirectories))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public async Task Verifying_never_adds_a_file_to_the_backup()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);
        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);
        var before = Listing(result.Directory);

        await _kit.Service.VerifyAsync(BackupKit.Human, result.Directory, ct);

        Assert.Equal(before, Listing(result.Directory));
    }

    [Fact]
    public async Task The_manifest_on_disk_is_the_documented_shape()
    {
        var ct = TestContext.Current.CancellationToken;
        await _kit.SeedAsync(ct);
        var result = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);

        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.Directory, "manifest.json")));
        var root = document.RootElement;

        Assert.Equal("mailcoded-backup", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("format_version").GetInt32());
        Assert.Equal("2026-01-15T12:00:00.000Z", root.GetProperty("created_utc").GetString());
        Assert.Equal("store.db", root.GetProperty("database").GetProperty("file").GetString());
        Assert.Equal(3, root.GetProperty("external_blobs").GetProperty("count").GetInt32());
        Assert.Equal(3, root.GetProperty("external_blobs").GetProperty("files").GetArrayLength());
        Assert.True(root.GetProperty("tables").TryGetProperty("messages", out _));
    }

    private static async Task<Exception?> Settle(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static List<string> Listing(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
}
