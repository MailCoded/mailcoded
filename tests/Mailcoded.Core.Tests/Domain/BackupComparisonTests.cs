using Mailcoded.Core.Domain.Backup;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class BackupComparisonTests
{
    private const int Latest = 11;
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);
    private static readonly string Db = new('d', 64);

    private static BackupManifest Manifest() => new()
    {
        MailcodedVersion = "0.1.0",
        SchemaVersion = Latest,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        DatabaseSha256 = Db,
        DatabaseBytes = 4096,
        Tables = [new BackupTableCount("accounts", 1), new BackupTableCount("messages", 10)],
        Blobs = [new BackupBlobEntry(ShaA, 100), new BackupBlobEntry(ShaB, 200)],
    };

    private static BackupObservation Clean() => new()
    {
        Database = new ObservedFile(true, 4096, Db),
        UserVersion = Latest,
        IntegrityMessages = ["ok"],
        TableCounts = new Dictionary<string, long> { ["accounts"] = 1, ["messages"] = 10 },
        DatabaseBlobHashes = [ShaA, ShaB],
        BlobFiles = new Dictionary<string, ObservedFile>
        {
            [ShaA] = new(true, 100, ShaA),
            [ShaB] = new(true, 200, ShaB),
        },
    };

    private static string[] Kinds(BackupObservation observed, BackupManifest? manifest = null) =>
        BackupComparison.Compare(manifest ?? Manifest(), observed, Latest).Select(static p => p.Kind).ToArray();

    [Fact]
    public void A_backup_that_matches_its_manifest_has_no_problems() => Assert.Empty(Kinds(Clean()));

    [Fact]
    public void A_wrong_database_hash_and_size_are_each_reported()
    {
        var observed = Clean() with { Database = new ObservedFile(true, 4097, new string('e', 64)) };

        Assert.Equal([BackupProblemKinds.DatabaseSize, BackupProblemKinds.DatabaseHash], Kinds(observed));
    }

    [Fact]
    public void A_missing_database_stops_the_database_derived_checks_from_producing_noise()
    {
        var observed = Clean() with { Database = new ObservedFile(false, 0, null), TableCounts = null, DatabaseBlobHashes = null, UserVersion = 0 };

        Assert.Equal([BackupProblemKinds.DatabaseMissing], Kinds(observed));
    }

    [Fact]
    public void An_unreadable_database_is_reported_with_its_reason_cleaned()
    {
        var observed = Clean() with { DatabaseError = "bad\u001b[2J", TableCounts = null, DatabaseBlobHashes = null };

        var problem = Assert.Single(BackupComparison.Compare(Manifest(), observed, Latest), static p => p.Kind == BackupProblemKinds.DatabaseUnreadable);
        Assert.DoesNotContain(problem.Message, static c => char.IsControl(c));
    }

    [Fact]
    public void Integrity_messages_other_than_ok_become_problems_and_are_capped()
    {
        var messages = Enumerable.Range(0, 50).Select(i => "row " + i + " missing from index").Prepend("ok").ToList();

        var problems = BackupComparison.Compare(Manifest(), Clean() with { IntegrityMessages = messages }, Latest);

        Assert.Equal(BackupComparison.MaxIntegrityMessages, problems.Count(static p => p.Kind == BackupProblemKinds.Integrity));
    }

    [Fact]
    public void A_schema_mismatch_and_a_newer_schema_are_distinguished()
    {
        Assert.Equal([BackupProblemKinds.SchemaVersion], Kinds(Clean() with { UserVersion = 9 }));

        var newer = Manifest() with { SchemaVersion = Latest + 1 };
        var kinds = Kinds(Clean() with { UserVersion = Latest + 1 }, newer);
        Assert.Equal([BackupProblemKinds.SchemaNewer], kinds);
    }

    [Fact]
    public void A_row_count_table_missing_or_unlisted_is_reported()
    {
        var observed = Clean() with
        {
            TableCounts = new Dictionary<string, long> { ["accounts"] = 2, ["surprise"] = 1 },
        };

        var problems = BackupComparison.Compare(Manifest(), observed, Latest);

        Assert.Contains(problems, static p => p.Kind == BackupProblemKinds.RowCount && p.Subject == "accounts");
        Assert.Contains(problems, static p => p.Kind == BackupProblemKinds.TableMissing && p.Subject == "messages");
        Assert.Contains(problems, static p => p.Kind == BackupProblemKinds.TableUnlisted && p.Subject == "surprise");
    }

    [Fact]
    public void Each_blob_failure_has_its_own_kind_and_names_the_file()
    {
        var observed = Clean() with
        {
            BlobFiles = new Dictionary<string, ObservedFile>
            {
                [ShaA] = new(true, 99, new string('9', 64)),
                [ShaB] = new(false, 0, null),
            },
        };

        var problems = BackupComparison.Compare(Manifest(), observed, Latest);

        Assert.Equal(
            [BackupProblemKinds.BlobSize, BackupProblemKinds.BlobHash, BackupProblemKinds.BlobMissing],
            problems.Select(static p => p.Kind).ToArray());
        Assert.All(problems, static p => Assert.StartsWith("blobs/", p.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public void A_blob_missing_from_the_observation_counts_as_missing()
    {
        var observed = Clean() with { BlobFiles = new Dictionary<string, ObservedFile>() };

        Assert.Equal(2, Kinds(observed).Count(static k => k == BackupProblemKinds.BlobMissing));
    }

    [Fact]
    public void The_database_and_the_manifest_must_agree_about_which_blobs_exist()
    {
        var other = new string('c', 64);

        var problems = BackupComparison.Compare(Manifest(), Clean() with { DatabaseBlobHashes = [ShaA, other] }, Latest);

        Assert.Contains(problems, p => p.Kind == BackupProblemKinds.BlobUnlisted && p.Subject == other);
        Assert.Contains(problems, p => p.Kind == BackupProblemKinds.BlobUnreferenced && p.Subject!.Contains(ShaB, StringComparison.Ordinal));
    }

    [Fact]
    public void Unexpected_files_links_and_floods_are_reported_with_clean_names()
    {
        var observed = Clean() with
        {
            UnexpectedFiles = ["notes.txt", "evil\u001b[2J\nname"],
            LinkEntries = ["blobs/aa/link"],
            TooManyFiles = true,
        };

        var problems = BackupComparison.Compare(Manifest(), observed, Latest);

        Assert.Equal(2, problems.Count(static p => p.Kind == BackupProblemKinds.UnexpectedFile));
        Assert.Single(problems, static p => p.Kind == BackupProblemKinds.LinkRefused);
        Assert.Single(problems, static p => p.Kind == BackupProblemKinds.TooManyFiles);
        Assert.All(problems, static p => Assert.DoesNotContain(p.Subject ?? string.Empty, static c => char.IsControl(c)));
    }

    [Fact]
    public void Only_the_manifest_the_database_and_the_listed_blobs_are_expected_paths()
    {
        var manifest = Manifest();

        Assert.True(BackupComparison.IsExpectedPath(manifest, "manifest.json"));
        Assert.True(BackupComparison.IsExpectedPath(manifest, "store.db"));
        Assert.True(BackupComparison.IsExpectedPath(manifest, "blobs/aa/" + ShaA + ".blob"));

        Assert.False(BackupComparison.IsExpectedPath(manifest, "store.db-wal"));
        Assert.False(BackupComparison.IsExpectedPath(manifest, "secrets.vault"));
        Assert.False(BackupComparison.IsExpectedPath(manifest, "blobs/cc/" + new string('c', 64) + ".blob"));
        Assert.False(BackupComparison.IsExpectedPath(manifest, "blobs/aa/" + ShaA + ".BLOB"));
        Assert.False(BackupComparison.IsExpectedPath(manifest, "Manifest.json"));
    }

    [Fact]
    public void Clean_replaces_control_characters_and_caps_length()
    {
        Assert.Equal("a?b", BackupComparison.Clean("a\u0000b"));
        Assert.Equal(120, BackupComparison.Clean(new string('x', 500)).Length);
        Assert.Equal(string.Empty, BackupComparison.Clean(null));
    }
}
