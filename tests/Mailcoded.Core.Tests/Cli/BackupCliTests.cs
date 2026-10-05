using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Application;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Backup;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class BackupCliTests : IDisposable
{
    private readonly SurfaceWorkspace _live = new("cli-backup-live");
    private readonly SurfaceWorkspace _target = new("cli-backup-target");

    public void Dispose()
    {
        _live.Dispose();
        _target.Dispose();
    }

    private string Dest(string name = "out") => Path.Combine(_target.Root, name);

    private static EnvironmentScope Unlocked() => EnvironmentScope.Set((AgentPolicyOptions.AllowBackupEnvVar, "1"));

    private static EnvironmentScope Locked() => EnvironmentScope.Set((AgentPolicyOptions.AllowBackupEnvVar, null));

    private async Task<IReadOnlyList<BlobRef>> SeedAsync(CancellationToken ct)
    {
        using var store = new SqliteStore(
            new SqliteStoreOptions { DatabasePath = _live.DatabasePath, InlineBlobThresholdBytes = 64 },
            new ManualClock());

        var account = await StoreSeed.AccountAsync(store, ct);
        var folder = await StoreSeed.FolderAsync(store, account, "INBOX", Mailcoded.Core.Domain.Primitives.FolderRole.Inbox, ct);
        await StoreSeed.MessageAsync(store, folder, StoreSeed.Envelope(1), ct, bodyText: "hello");

        return [await store.StoreBlobAsync(BackupKit.Payload(900, 1), ct), await store.StoreBlobAsync(BackupKit.Payload(1900, 2), ct)];
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments, CancellationToken ct, bool json = true) =>
        CliRunner.RunAsync(_live.DatabasePath, verb, arguments, ct, json);

    [Fact]
    public async Task Without_the_unlock_the_verb_is_forbidden_with_a_hint_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Locked();

        var run = await RunAsync("backup", [Dest()], ct);

        Assert.Equal(ExitCodes.Forbidden, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdErr);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(1006, error.GetProperty("code").GetInt32());
        Assert.Contains("MAILCODED_ALLOW_BACKUP=1", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("MAILCODED_ALLOW_BACKUP=1", error.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dest()));
    }

    [Fact]
    public async Task Verification_without_the_unlock_is_forbidden_too()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Locked();

        Assert.Equal(ExitCodes.Forbidden, (await RunAsync("backup-verify", [_target.Root], ct)).ExitCode);
    }

    [Fact]
    public async Task With_the_unlock_a_backup_is_written_and_described_and_verifies()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = await SeedAsync(ct);
        using var gate = Unlocked();

        var run = await RunAsync("backup", [Dest()], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("backup", root.GetProperty("command").GetString());
        Assert.Equal(Dest(), root.GetProperty("directory").GetString());
        Assert.Equal(Path.Combine(Dest(), "manifest.json"), root.GetProperty("manifest").GetString());
        Assert.Equal(1, root.GetProperty("format_version").GetInt32());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(SqliteStore.LatestSchemaVersion, root.GetProperty("store_schema_version").GetInt32());
        Assert.Equal(64, root.GetProperty("database_sha256").GetString()!.Length);
        Assert.Equal(blobs.Count, root.GetProperty("external_blob_count").GetInt32());
        Assert.Equal(blobs.Sum(static b => b.Size), root.GetProperty("external_blob_bytes").GetInt64());
        Assert.Equal(1, root.GetProperty("tables").GetProperty("messages").GetInt32());
        Assert.False(root.GetProperty("secrets_included").GetBoolean());
        Assert.Contains("signed in again", root.GetProperty("note").GetString(), StringComparison.Ordinal);

        var verify = await RunAsync("backup-verify", [Dest()], ct);
        Assert.Equal(ExitCodes.Ok, verify.ExitCode);
        using var verified = JsonDocument.Parse(verify.StdOut);
        Assert.True(verified.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, verified.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(SqliteStore.LatestSchemaVersion, verified.RootElement.GetProperty("store_schema_version").GetInt32());
        Assert.Equal(0, verified.RootElement.GetProperty("problem_count").GetInt32());
        Assert.Equal("ok", verified.RootElement.GetProperty("integrity").GetString());
        Assert.Equal(blobs.Count, verified.RootElement.GetProperty("blob_files_checked").GetInt32());
    }

    [Fact]
    public async Task A_tampered_backup_exits_nine_with_ok_false_and_every_problem_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        var blobs = await SeedAsync(ct);
        using var gate = Unlocked();
        Assert.Equal(ExitCodes.Ok, (await RunAsync("backup", [Dest()], ct)).ExitCode);

        BackupKit.FlipByte(BackupKit.BlobPath(Dest(), blobs[0].Sha256), 3);
        File.WriteAllText(Path.Combine(Dest(), "extra.txt"), "x");

        var run = await RunAsync("backup-verify", [Dest()], ct);

        Assert.Equal(ExitCodes.Store, run.ExitCode);
        using var document = JsonDocument.Parse(run.StdOut);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        var kinds = document.RootElement.GetProperty("problems").EnumerateArray().Select(static p => p.GetProperty("kind").GetString()).ToList();
        Assert.Contains("blob_hash", kinds);
        Assert.Contains("unexpected_file", kinds);
        Assert.Equal(kinds.Count, document.RootElement.GetProperty("problem_count").GetInt32());
        Assert.False(document.RootElement.GetProperty("problems_truncated").GetBoolean());
    }

    [Fact]
    public async Task The_human_text_form_says_where_what_and_how_to_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Unlocked();

        var run = await RunAsync("backup", [Dest()], ct, json: false);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        Assert.Contains("Backup written to", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("signed in again", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("backup-verify", run.StdOut, StringComparison.Ordinal);

        var verify = await RunAsync("backup-verify", [Dest()], ct, json: false);
        Assert.Contains("matches its manifest", verify.StdOut, StringComparison.Ordinal);

        File.AppendAllText(Path.Combine(Dest(), "store.db"), "x");
        var failed = await RunAsync("backup-verify", [Dest()], ct, json: false);
        Assert.Equal(ExitCodes.Store, failed.ExitCode);
        Assert.Contains("does NOT match", failed.StdOut, StringComparison.Ordinal);
        Assert.Contains("[database_hash]", failed.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relative_destination_is_resolved_against_the_working_directory()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Unlocked();
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Dest("rel"));

        var run = await RunAsync("backup", [relative], ct);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(Dest("rel"), "manifest.json")));
    }

    [Fact]
    public async Task Destinations_that_are_not_acceptable_are_validation_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Unlocked();

        Directory.CreateDirectory(Dest("busy"));
        File.WriteAllText(Path.Combine(Dest("busy"), "x"), "x");

        Assert.Equal(ExitCodes.Validation, (await RunAsync("backup", [], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync("backup", [Dest("a"), Dest("b")], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync("backup", [Dest("busy")], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync("backup", [Path.Combine(_live.Root, "backups")], ct)).ExitCode);
        Assert.Equal(ExitCodes.Validation, (await RunAsync("backup", [Path.Combine(_target.Root, "no", "parent")], ct)).ExitCode);
    }

    [Fact]
    public async Task A_destination_inside_a_workspace_folder_is_refused_and_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Unlocked();
        Directory.CreateDirectory(Path.Combine(_target.Root, "project", ".git"));

        var run = await RunAsync("backup", [Path.Combine(_target.Root, "project", "mail")], ct);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        Assert.Contains("workspace", run.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_destination_name_with_an_escape_sequence_never_reaches_the_terminal_raw()
    {
        if (OperatingSystem.IsWindows()) return;

        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Unlocked();

        var run = await RunAsync("backup", [Path.Combine(_target.Root, "evil\u001b[31mred")], ct, json: false);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        Assert.DoesNotContain('\u001b', run.StdErr);
        Assert.DoesNotContain('\u001b', run.StdOut);
    }

    [Fact]
    public async Task A_hostile_file_name_in_a_backup_never_reaches_the_terminal_raw()
    {
        if (OperatingSystem.IsWindows()) return;

        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using var gate = Unlocked();
        await RunAsync("backup", [Dest()], ct);
        File.WriteAllText(Path.Combine(Dest(), "evil\u001b[2Jname"), "x");

        var text = await RunAsync("backup-verify", [Dest()], ct, json: false);
        var json = await RunAsync("backup-verify", [Dest()], ct);

        Assert.Equal(ExitCodes.Store, text.ExitCode);
        Assert.DoesNotContain('\u001b', text.StdOut);
        Assert.DoesNotContain('\u001b', json.StdOut);
    }

    [Fact]
    public async Task Both_verbs_write_audit_rows_from_the_cli()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        using (Unlocked())
        {
            await RunAsync("backup", [Dest()], ct);
            await RunAsync("backup-verify", [Dest()], ct);
        }

        using (Locked()) await RunAsync("backup", [Dest("again")], ct);

        using var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _live.DatabasePath }, new ManualClock());
        var rows = store.ReadSyncLog(limit: 100, ct: ct).Items.Where(static r => r.Event.StartsWith("backup_", StringComparison.Ordinal)).OrderBy(static r => r.Id).ToList();

        Assert.Equal(
            ["backup_started", "backup_completed", "backup_verified", "backup_denied"],
            rows.Select(static r => r.Event).ToArray());
        Assert.All(rows, static r => Assert.Equal("cli", r.Interface));
    }

    [Fact]
    public void The_verbs_are_in_the_table_and_the_help_explains_gate_restore_and_secrets()
    {
        Assert.NotNull(Mailcoded.Cli.Commands.CommandTable.Find("backup"));
        Assert.NotNull(Mailcoded.Cli.Commands.CommandTable.Find("backup-verify"));

        var help = HelpText.For("backup");
        Assert.Contains("MAILCODED_ALLOW_BACKUP=1", help, StringComparison.Ordinal);
        Assert.Contains("RESTORE", help, StringComparison.Ordinal);
        Assert.Contains("signed in", help, StringComparison.Ordinal);
        Assert.Contains("workspace", help, StringComparison.Ordinal);
        Assert.Contains("backup-verify", HelpText.Overview, StringComparison.Ordinal);
        Assert.Contains("MAILCODED_ALLOW_BACKUP", HelpText.Overview, StringComparison.Ordinal);
        Assert.Contains("integrity_check", HelpText.For("backup-verify"), StringComparison.Ordinal);
    }
}
