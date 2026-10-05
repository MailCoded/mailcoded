using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Backup;
using Mailcoded.Core.Store;
using Xunit;

namespace Mailcoded.Core.Tests.Backup;

public sealed class BackupTamperTests : IDisposable
{
    private readonly BackupKit _kit = BackupKit.Create();
    private BackupResult? _backup;
    private IReadOnlyList<BlobRef> _blobs = [];

    public void Dispose() => _kit.Dispose();

    private string Root => _backup!.Directory;

    private string DatabasePath => Path.Combine(Root, "store.db");

    private string ManifestPath => Path.Combine(Root, "manifest.json");

    private string FirstBlobPath => BackupKit.BlobPath(Root, _backup!.Manifest.Blobs.OrderBy(static b => b.Sha256, StringComparer.Ordinal).First().Sha256);

    private async Task InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _blobs = await _kit.SeedAsync(ct);
        _backup = await _kit.Service.CreateAsync(BackupKit.Human, _kit.Dest(), ct);
    }

    private async Task<BackupVerification> VerifyAsync() =>
        await _kit.Service.VerifyAsync(BackupKit.Human, Root, TestContext.Current.CancellationToken);

    private static string[] Kinds(BackupVerification result) => result.Problems.Select(static p => p.Kind).Distinct().Order(StringComparer.Ordinal).ToArray();

    private void EditManifest(Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
        edit(node);
        File.WriteAllText(ManifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task A_flipped_byte_in_the_database_is_reported()
    {
        await InitializeAsync();
        BackupKit.FlipByte(DatabasePath, new FileInfo(DatabasePath).Length / 2);

        var result = await VerifyAsync();

        Assert.False(result.Ok);
        Assert.Contains(BackupProblemKinds.DatabaseHash, Kinds(result));
    }

    [Fact]
    public async Task A_database_that_was_appended_to_changes_its_size_and_hash()
    {
        await InitializeAsync();
        await using (var stream = new FileStream(DatabasePath, FileMode.Append)) stream.Write([1, 2, 3]);

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.DatabaseSize, kinds);
        Assert.Contains(BackupProblemKinds.DatabaseHash, kinds);
    }

    [Fact]
    public async Task A_flipped_byte_in_a_blob_is_reported_with_its_path()
    {
        await InitializeAsync();
        BackupKit.FlipByte(FirstBlobPath, 5);

        var result = await VerifyAsync();

        var problem = Assert.Single(result.Problems, static p => p.Kind == BackupProblemKinds.BlobHash);
        Assert.StartsWith("blobs/", problem.Subject, StringComparison.Ordinal);
        Assert.Equal(1, result.ProblemCount);
    }

    [Fact]
    public async Task A_truncated_blob_is_reported_for_size_and_hash()
    {
        await InitializeAsync();
        await using (var stream = new FileStream(FirstBlobPath, FileMode.Open)) stream.SetLength(10);

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.BlobSize, kinds);
        Assert.Contains(BackupProblemKinds.BlobHash, kinds);
    }

    [Fact]
    public async Task A_missing_blob_is_reported()
    {
        await InitializeAsync();
        File.Delete(FirstBlobPath);

        Assert.Equal([BackupProblemKinds.BlobMissing], Kinds(await VerifyAsync()));
    }

    [Fact]
    public async Task A_missing_database_is_reported()
    {
        await InitializeAsync();
        File.Delete(DatabasePath);

        Assert.Contains(BackupProblemKinds.DatabaseMissing, Kinds(await VerifyAsync()));
    }

    [Fact]
    public async Task Every_mismatch_is_reported_not_just_the_first()
    {
        await InitializeAsync();
        BackupKit.FlipByte(DatabasePath, 300);
        BackupKit.FlipByte(FirstBlobPath, 1);
        File.WriteAllText(Path.Combine(Root, "notes.txt"), "extra");

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.DatabaseHash, kinds);
        Assert.Contains(BackupProblemKinds.BlobHash, kinds);
        Assert.Contains(BackupProblemKinds.UnexpectedFile, kinds);
    }

    [Fact]
    public async Task An_edited_row_count_is_reported_against_the_real_table()
    {
        await InitializeAsync();
        EditManifest(root => root["tables"]!["messages"] = 5);

        var result = await VerifyAsync();

        var problem = Assert.Single(result.Problems);
        Assert.Equal(BackupProblemKinds.RowCount, problem.Kind);
        Assert.Equal("messages", problem.Subject);
    }

    [Fact]
    public async Task An_edited_database_hash_is_reported()
    {
        await InitializeAsync();
        EditManifest(root => root["database"]!["sha256"] = new string('a', 64));

        Assert.Equal([BackupProblemKinds.DatabaseHash], Kinds(await VerifyAsync()));
    }

    [Fact]
    public async Task An_edited_schema_version_is_reported()
    {
        await InitializeAsync();
        EditManifest(root => root["schema_version"] = 3);

        Assert.Contains(BackupProblemKinds.SchemaVersion, Kinds(await VerifyAsync()));
    }

    [Fact]
    public async Task A_manifest_from_a_newer_schema_says_so()
    {
        await InitializeAsync();
        EditManifest(root => root["schema_version"] = SqliteStore.LatestSchemaVersion + 1);

        Assert.Contains(BackupProblemKinds.SchemaNewer, Kinds(await VerifyAsync()));
    }

    [Fact]
    public async Task A_dropped_blob_entry_makes_the_manifest_inconsistent_and_a_forged_one_is_caught_too()
    {
        await InitializeAsync();
        EditManifest(root => root["external_blobs"]!["files"]!.AsArray().RemoveAt(0));
        Assert.Equal([BackupProblemKinds.ManifestInvalid], Kinds(await VerifyAsync()));

        EditManifest(root =>
        {
            var blobs = root["external_blobs"]!.AsObject();
            blobs["files"]!.AsArray().RemoveAt(0);
            blobs["count"] = blobs["files"]!.AsArray().Count;
            blobs["bytes"] = blobs["files"]!.AsArray().Sum(static f => (long)f!["bytes"]!);
        });

        var kinds = Kinds(await VerifyAsync());
        Assert.Contains(BackupProblemKinds.BlobUnlisted, kinds);
        Assert.Contains(BackupProblemKinds.UnexpectedFile, kinds);
    }

    [Fact]
    public async Task A_manifest_entry_for_a_blob_the_database_does_not_use_is_reported()
    {
        await InitializeAsync();
        var phantom = new string('c', 64);
        EditManifest(root =>
        {
            var blobs = root["external_blobs"]!.AsObject();
            blobs["files"]!.AsArray().Add(new JsonObject { ["sha256"] = phantom, ["bytes"] = 5 });
            blobs["count"] = (long)blobs["count"]! + 1;
            blobs["bytes"] = (long)blobs["bytes"]! + 5;
        });

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.BlobMissing, kinds);
        Assert.Contains(BackupProblemKinds.BlobUnreferenced, kinds);
    }

    [Fact]
    public async Task A_manifest_that_is_gone_or_not_json_or_from_a_newer_format_is_named_as_such()
    {
        await InitializeAsync();
        var original = File.ReadAllBytes(ManifestPath);

        File.WriteAllText(ManifestPath, "this is not json");
        Assert.Equal([BackupProblemKinds.ManifestInvalid], Kinds(await VerifyAsync()));

        EditFormat(2);
        Assert.Equal([BackupProblemKinds.UnsupportedFormat], Kinds(await VerifyAsync()));

        File.Delete(ManifestPath);
        Assert.Equal([BackupProblemKinds.ManifestMissing], Kinds(await VerifyAsync()));

        File.WriteAllBytes(ManifestPath, original);
        Assert.True((await VerifyAsync()).Ok);

        void EditFormat(int version)
        {
            File.WriteAllBytes(ManifestPath, original);
            EditManifest(root => root["format_version"] = version);
        }
    }

    [Fact]
    public async Task A_file_nobody_listed_is_reported()
    {
        await InitializeAsync();
        File.WriteAllText(Path.Combine(Root, "store.db-wal"), "x");
        Directory.CreateDirectory(Path.Combine(Root, "blobs", "zz"));
        File.WriteAllText(Path.Combine(Root, "blobs", "zz", "stray.blob"), "x");

        var unexpected = (await VerifyAsync()).Problems.Where(static p => p.Kind == BackupProblemKinds.UnexpectedFile).Select(static p => p.Subject).ToList();

        Assert.Contains("store.db-wal", unexpected);
        Assert.Contains("blobs/zz/stray.blob", unexpected);
    }

    [Fact]
    public async Task A_blob_swapped_for_a_symbolic_link_is_not_followed()
    {
        await InitializeAsync();
        var outside = Path.Combine(_kit.Target.Root, "outside.bin");
        File.WriteAllBytes(outside, File.ReadAllBytes(FirstBlobPath));
        File.Delete(FirstBlobPath);
        if (!TryLink(FirstBlobPath, outside)) return;

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.LinkRefused, kinds);
        Assert.Contains(BackupProblemKinds.BlobMissing, kinds);
    }

    [Fact]
    public async Task A_database_swapped_for_a_symbolic_link_is_not_followed()
    {
        await InitializeAsync();
        var outside = Path.Combine(_kit.Target.Root, "outside.db");
        File.Copy(DatabasePath, outside);
        File.Delete(DatabasePath);
        if (!TryLink(DatabasePath, outside)) return;

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.LinkRefused, kinds);
        Assert.Contains(BackupProblemKinds.DatabaseMissing, kinds);
    }

    [Fact]
    public async Task A_database_replaced_by_garbage_of_the_same_size_is_unreadable_not_a_crash()
    {
        await InitializeAsync();
        var length = new FileInfo(DatabasePath).Length;
        File.WriteAllBytes(DatabasePath, new byte[length]);

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.DatabaseHash, kinds);
    }

    [Fact]
    public async Task A_missing_directory_is_a_problem_not_an_exception()
    {
        var result = await _kit.Service.VerifyAsync(BackupKit.Human, _kit.Dest("nowhere"), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(BackupProblemKinds.DirectoryMissing, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public async Task A_directory_flooded_with_files_stops_the_walk()
    {
        await InitializeAsync();
        var flood = Path.Combine(Root, "flood");
        Directory.CreateDirectory(flood);
        for (var i = 0; i < 1_200; i++) File.WriteAllBytes(Path.Combine(flood, i + ".x"), []);

        var kinds = Kinds(await VerifyAsync());

        Assert.Contains(BackupProblemKinds.TooManyFiles, kinds);
    }

    [Fact]
    public async Task A_hostile_file_name_cannot_inject_control_characters_into_the_report()
    {
        await InitializeAsync();
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(Root, "evil\u001b[2J\nname"), "x");

        var problem = Assert.Single((await VerifyAsync()).Problems, static p => p.Kind == BackupProblemKinds.UnexpectedFile);

        Assert.DoesNotContain(problem.Subject!, static c => char.IsControl(c));
    }

    private static bool TryLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
