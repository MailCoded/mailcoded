using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class CliStoreFixture : IAsyncLifetime
{
    private SurfaceWorkspace? _workspace;

    public string DatabasePath =>
        (_workspace ?? throw new InvalidOperationException("The CLI fixture was not initialized.")).DatabasePath;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = new SurfaceWorkspace("cli-surface");
        _workspace = workspace;

        using var store = new SqliteStore(
            new SqliteStoreOptions { DatabasePath = workspace.DatabasePath },
            new ManualClock());

        var accountId = await StoreSeeder.AddAccountAsync(store, ct);
        var folderId = await StoreSeeder.AddInboxAsync(store, accountId, ct);

        for (var i = 1; i <= 3; i++)
        {
            await StoreSeeder.AddMessageAsync(
                store,
                folderId,
                uid: (uint)i,
                messageId: $"cli-{i}@example.test",
                subject: $"Quarterly invoice {i}",
                from: "Ada <ada@example.test>",
                to: "golden@example.test",
                dateUtc: new DateTimeOffset(2024, 1, i, 0, 0, 0, TimeSpan.Zero),
                bodyText: $"The invoice total is {i} dollars.",
                size: 1000 + i,
                ct: ct);
        }

        await store.RecountFolderAsync(folderId, ct);
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        _workspace = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class CliSurfaceTests : IClassFixture<CliStoreFixture>
{
    private readonly CliStoreFixture _store;

    public CliSurfaceTests(CliStoreFixture store) => _store = store;

    [Fact]
    public async Task Search_SucceedsAndItsJsonCarriesTheSchemaVersion()
    {
        var run = await RunAsync("search", ["invoice"]);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        Assert.Equal(string.Empty, run.StdErr);

        using var document = JsonDocument.Parse(run.StdOut);
        var root = document.RootElement;

        Assert.Equal(CliOutput.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("search", root.GetProperty("command").GetString());
        Assert.Equal(3, root.GetProperty("hits").GetArrayLength());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task Search_APaginatedPageCarriesNextCursorAndAnExplicitTruncatedFlag()
    {
        var first = await RunAsync("search", ["invoice", "--limit", "1"]);
        Assert.Equal(ExitCodes.Ok, first.ExitCode);

        using var page = JsonDocument.Parse(first.StdOut);
        var root = page.RootElement;

        Assert.Equal(1, root.GetProperty("hits").GetArrayLength());
        Assert.True(root.GetProperty("truncated").GetBoolean());

        var cursor = root.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var firstId = root.GetProperty("hits")[0].GetProperty("id").GetInt64();

        var second = await RunAsync("search", ["invoice", "--limit", "1", "--cursor", cursor!]);
        Assert.Equal(ExitCodes.Ok, second.ExitCode);

        using var next = JsonDocument.Parse(second.StdOut);
        Assert.Equal(1, next.RootElement.GetProperty("hits").GetArrayLength());
        Assert.NotEqual(firstId, next.RootElement.GetProperty("hits")[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Read_ReturnsPlaintextOnlyAndReportsItsOwnPaging()
    {
        var run = await RunAsync("read", ["1", "--no-fetch"]);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        using var document = JsonDocument.Parse(run.StdOut);
        var root = document.RootElement;

        Assert.Equal(CliOutput.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("plaintext", root.GetProperty("body_format").GetString());
        Assert.Contains("invoice", root.GetProperty("body_text").GetString()!, StringComparison.Ordinal);
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.TryGetProperty("next_cursor", out _));

        foreach (var property in root.EnumerateObject())
        {
            Assert.DoesNotContain("html", property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Read_TruncatesAndOffersACursorWhenTheBodyIsLongerThanTheWindow()
    {
        var run = await RunAsync("read", ["1", "--no-fetch", "--max-chars", "5"]);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        using var document = JsonDocument.Parse(run.StdOut);
        var root = document.RootElement;

        Assert.Equal(5, root.GetProperty("body_chars").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal("5", root.GetProperty("next_cursor").GetString());
    }

    [Fact]
    public async Task Folders_SucceedsWithExitCodeZero()
    {
        var run = await RunAsync("folders", []);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        using var document = JsonDocument.Parse(run.StdOut);
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task InvalidArguments_ExitWithTheValidationCode()
    {
        var run = await RunAsync("search", ["invoice", "--limit", "0"]);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        AssertErrorDocument(run, ExitCodes.Validation, "invalid_params");
    }

    [Fact]
    public async Task AMissingMessage_ExitsWithTheNotFoundCode()
    {
        var run = await RunAsync("read", ["999999", "--no-fetch"]);

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        AssertErrorDocument(run, ExitCodes.NotFound, "not_found");
    }

    [Fact]
    public async Task RawSql_ExitsWithThePermissionCodeUntilTheEnvironmentUnlocksIt()
    {
        CliRun denied;
        using (EnvironmentScope.Set((AgentPolicyOptions.EnableSqlEnvVar, null)))
        {
            denied = await RunAsync("query", ["--sql", "SELECT id FROM messages"]);
        }

        Assert.Equal(ExitCodes.Forbidden, denied.ExitCode);
        AssertErrorDocument(denied, ExitCodes.Forbidden, "forbidden");

        CliRun allowed;
        using (EnvironmentScope.Set((AgentPolicyOptions.EnableSqlEnvVar, "1")))
        {
            allowed = await RunAsync("query", ["--sql", "SELECT id FROM messages ORDER BY id", "--max-rows", "2"]);
        }

        Assert.Equal(ExitCodes.Ok, allowed.ExitCode);

        using var document = JsonDocument.Parse(allowed.StdOut);
        var root = document.RootElement;

        Assert.Equal(CliOutput.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(2, root.GetProperty("row_count").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task SendDraft_WithoutTheConfirmTokenOptionIsAValidationError()
    {
        var run = await RunAsync("send-draft", ["1"]);

        Assert.Equal(ExitCodes.Validation, run.ExitCode);
        AssertErrorDocument(run, ExitCodes.Validation, "invalid_params");
        Assert.Contains("confirm-token", run.StdErr, StringComparison.Ordinal);
    }

    private static void AssertErrorDocument(CliRun run, int exitCode, string name)
    {
        Assert.Equal(string.Empty, run.StdOut);

        using var document = JsonDocument.Parse(run.StdErr);
        var root = document.RootElement;

        Assert.Equal(CliOutput.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.False(root.GetProperty("ok").GetBoolean());

        var error = root.GetProperty("error");
        Assert.Equal(name, error.GetProperty("name").GetString());
        Assert.Equal(exitCode, error.GetProperty("exit_code").GetInt32());
        Assert.True(error.GetProperty("code").GetInt32() != 0);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments) =>
        CliRunner.RunAsync(_store.DatabasePath, verb, arguments, TestContext.Current.CancellationToken);
}
