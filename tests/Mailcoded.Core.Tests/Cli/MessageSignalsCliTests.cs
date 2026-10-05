using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class MessageSignalsCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-signals");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Imported_mail_is_searchable_by_signal_and_reports_it_in_json()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);

        foreach (var name in new[] { "001-plain-simple.eml", "038-list-mail.eml", "041-pgp-mime.eml", "043-inline-pgp.eml" })
            File.WriteAllBytes(Path.Combine(directory, name), Fixtures.Read(name));

        var import = await RunAsync("import-eml", [directory], ct);
        Assert.Equal(ExitCodes.Ok, import.ExitCode);

        var automated = Subjects(await RunAsync("search", ["is:automated", "--order", "date"], ct));
        Assert.Equal(["[announce] Weekly digest"], automated);

        var encrypted = Subjects(await RunAsync("search", ["is:encrypted", "--order", "date"], ct));
        Assert.Equal(2, encrypted.Count);
        Assert.Contains("Encrypted message (PGP/MIME)", encrypted);
        Assert.Contains("Inline armored message", encrypted);

        var human = Subjects(await RunAsync("search", ["-is:automated -is:encrypted", "--order", "date"], ct));
        Assert.Single(human);

        using var all = JsonDocument.Parse((await RunAsync("search", ["", "--order", "date"], ct)).StdOut);
        foreach (var hit in all.RootElement.GetProperty("hits").EnumerateArray())
        {
            Assert.True(hit.TryGetProperty("automated", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False);
            Assert.True(hit.TryGetProperty("encrypted", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False);
        }
    }

    [Fact]
    public async Task Read_reports_the_signals()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "038-list-mail.eml"), Fixtures.Read("038-list-mail.eml"));

        Assert.Equal(ExitCodes.Ok, (await RunAsync("import-eml", [directory], ct)).ExitCode);

        var read = await RunAsync("read", ["1", "--no-fetch"], ct);
        using var document = JsonDocument.Parse(read.StdOut);

        Assert.True(document.RootElement.GetProperty("automated").GetBoolean());
        Assert.False(document.RootElement.GetProperty("encrypted").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_signal_is_an_explicit_null_never_false()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = new Mailcoded.Core.Store.SqliteStore(
            new Mailcoded.Core.Store.SqliteStoreOptions { DatabasePath = _workspace.DatabasePath },
            new ManualClock());

        var account = await StoreSeeder.AddAccountAsync(store, ct);
        var folder = await StoreSeeder.AddInboxAsync(store, account, ct);
        await StoreSeeder.AddMessageAsync(
            store, folder, 1, "legacy@example.test", "Legacy invoice", "Ada <ada@example.test>", "golden@example.test",
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "invoice body", 100, ct);
        store.Dispose();

        using var document = JsonDocument.Parse((await RunAsync("read", ["1", "--no-fetch"], ct)).StdOut);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("automated").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("encrypted").ValueKind);

        var search = await RunAsync("search", ["-is:automated"], ct);
        using var result = JsonDocument.Parse(search.StdOut);
        Assert.Equal(0, result.RootElement.GetProperty("hits").GetArrayLength());
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments, CancellationToken ct) =>
        CliRunner.RunAsync(_workspace.DatabasePath, verb, arguments, ct);

    private static List<string> Subjects(CliRun run)
    {
        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        using var document = JsonDocument.Parse(run.StdOut);
        var subjects = new List<string>();
        foreach (var hit in document.RootElement.GetProperty("hits").EnumerateArray())
            subjects.Add(hit.GetProperty("subject").GetString() ?? string.Empty);

        return subjects;
    }
}
