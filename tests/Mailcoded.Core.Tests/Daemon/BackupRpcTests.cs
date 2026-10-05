using System.Text;
using System.Text.Json;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class BackupRpcTests
{
    private const string Placeholder = "$BACKUP_DIR$";

    [Theory]
    [InlineData("store-backup")]
    [InlineData("store-backup-verify")]
    [InlineData("store-backup-verify-missing")]
    public async Task Transcript_AfterInitializeAsAHumanClient_MatchesItsGoldenFile(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);
        var destination = Path.Combine(Path.GetTempPath(), "mailcoded-rpc-backup-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (name == "store-backup-verify") await Exchange(transcript, "store-backup", destination, "rpc", ct);

            var actual = GoldenJson.Canonicalize(await Exchange(transcript, name, destination, "rpc", ct), transcript.StoreDirectory).TrimEnd('\n');

            if (GoldenFiles.UpdateRequested())
            {
                GoldenFiles.WriteResponse(name + ".response.json", actual);
                return;
            }

            var expected = GoldenFiles.ReadResponse(name + ".response.json").TrimEnd('\n');
            Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), GoldenJson.Describe(name, expected, actual));
        }
        finally
        {
            TryDelete(destination);
        }
    }

    [Fact]
    public async Task A_human_client_gets_a_backup_whose_wire_shape_matches_the_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);
        var destination = Path.Combine(Path.GetTempPath(), "mailcoded-rpc-backup-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var document = JsonDocument.Parse(await Exchange(transcript, "store-backup", destination, "rpc", ct));
            var result = document.RootElement.GetProperty("result");
            var backup = result.GetProperty("backup");

            Assert.False(result.GetProperty("secretsIncluded").GetBoolean());
            Assert.Equal(destination, backup.GetProperty("directory").GetString());
            Assert.Equal(1, backup.GetProperty("formatVersion").GetInt32());
            Assert.Equal(64, backup.GetProperty("databaseSha256").GetString()!.Length);
            Assert.Equal(4, backup.GetProperty("tableCounts").GetProperty("messages").GetInt64());
            Assert.Equal(0, backup.GetProperty("externalBlobCount").GetInt64());
            Assert.True(File.Exists(Path.Combine(destination, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(destination, "store.db")));
        }
        finally
        {
            TryDelete(destination);
        }
    }

    [Fact]
    public async Task A_mismatch_is_a_result_with_every_problem_not_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);
        var destination = Path.Combine(Path.GetTempPath(), "mailcoded-rpc-backup-" + Guid.NewGuid().ToString("N"));

        try
        {
            await Exchange(transcript, "store-backup", destination, "rpc", ct);
            await File.AppendAllTextAsync(Path.Combine(destination, "store.db"), "x", ct);
            await File.WriteAllTextAsync(Path.Combine(destination, "stray.txt"), "x", ct);

            using var document = JsonDocument.Parse(await Exchange(transcript, "store-backup-verify", destination, "rpc", ct));

            Assert.False(document.RootElement.TryGetProperty("error", out _));
            var result = document.RootElement.GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            var kinds = result.GetProperty("problems").EnumerateArray().Select(static p => p.GetProperty("kind").GetString()).ToList();
            Assert.Contains("database_size", kinds);
            Assert.Contains("database_hash", kinds);
            Assert.Contains("unexpected_file", kinds);
            Assert.Equal(kinds.Count, result.GetProperty("problemCount").GetInt32());
        }
        finally
        {
            TryDelete(destination);
        }
    }

    [Fact]
    public async Task The_capability_flag_follows_the_caller_and_the_unlock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var locked = await DaemonTranscript.StartAsync(ct);
        await using var unlocked = await DaemonTranscript.StartAsync(ct, backupUnlocked: true);

        Assert.True(await CapabilityAsync(locked, "rpc", ct));
        Assert.False(await CapabilityAsync(locked, "cli", ct));
        Assert.False(await CapabilityAsync(locked, "mcp", ct));
        Assert.True(await CapabilityAsync(unlocked, "cli", ct));
        Assert.False(await CapabilityAsync(unlocked, "mcp", ct));
    }

    [Theory]
    [InlineData("cli", false)]
    [InlineData("mcp", false)]
    [InlineData("mcp", true)]
    public async Task A_client_that_declared_itself_an_agent_is_refused_unless_unlocked_and_never_over_mcp(string callerKind, bool unlocked)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct, backupUnlocked: unlocked);
        var destination = Path.Combine(Path.GetTempPath(), "mailcoded-rpc-backup-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var document = JsonDocument.Parse(await Exchange(transcript, "store-backup", destination, callerKind, ct));

            Assert.Equal((int)RpcErrorCode.Forbidden, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            TryDelete(destination);
        }
    }

    [Fact]
    public async Task An_unlocked_cli_declared_client_may_back_up()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct, backupUnlocked: true);
        var destination = Path.Combine(Path.GetTempPath(), "mailcoded-rpc-backup-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var document = JsonDocument.Parse(await Exchange(transcript, "store-backup", destination, "cli", ct));

            Assert.True(document.RootElement.TryGetProperty("result", out _));
        }
        finally
        {
            TryDelete(destination);
        }
    }

    [Fact]
    public async Task Bad_destinations_are_invalid_params()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        foreach (var directory in new[] { "relative/dir", "", Path.Combine(transcript.StoreDirectory, "backups"), transcript.StoreDirectory })
        {
            var request = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "store.backup",
                ["params"] = new Dictionary<string, string> { ["directory"] = directory },
            });

            using var document = JsonDocument.Parse(await transcript.ExchangeAsync(Encoding.UTF8.GetBytes(request), "rpc", ct));

            Assert.Equal(
                (int)RpcErrorCode.InvalidParams,
                document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task A_request_without_a_directory_is_invalid_params()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = JsonDocument.Parse(await transcript.ExchangeAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"store.backup","params":{}}"""), "rpc", ct));

        Assert.Equal((int)RpcErrorCode.InvalidParams, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task The_methods_are_advertised_in_initialize()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = JsonDocument.Parse(await transcript.ExchangeAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientName":"t","clientVersion":"0"}}"""), ct));

        var methods = document.RootElement.GetProperty("result").GetProperty("capabilities").GetProperty("methods")
            .EnumerateArray().Select(static m => m.GetString()).ToList();

        Assert.Contains(RpcMethods.StoreBackup, methods);
        Assert.Contains(RpcMethods.StoreBackupVerify, methods);
    }

    private static async Task<string> Exchange(DaemonTranscript transcript, string name, string destination, string asInterface, CancellationToken ct)
    {
        var request = Encoding.UTF8.GetString(GoldenFiles.ReadRequest(name + ".request.json"))
            .Replace(Placeholder, destination.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal);

        return await transcript.ExchangeAsync(Encoding.UTF8.GetBytes(request), asInterface, ct);
    }

    private static async Task<bool> CapabilityAsync(DaemonTranscript transcript, string asInterface, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientName":"t","clientVersion":"0","interface":"__I__"}}""".Replace("__I__", asInterface, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(await transcript.ExchangeAsync(body, ct));
        return document.RootElement.GetProperty("result").GetProperty("capabilities").GetProperty("backup").GetBoolean();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
