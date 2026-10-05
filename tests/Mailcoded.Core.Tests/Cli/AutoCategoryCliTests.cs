using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class AutoCategoryCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-auto-category");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Imported_mail_is_categorised_once_and_listed_and_found_through_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        Import(["001-plain-simple.eml", "038-list-mail.eml", "049-promo-mail.eml"]);

        Assert.Equal(ExitCodes.Ok, (await RunAsync("import-eml", [_workspace.PathTo("eml")], ct)).ExitCode);

        Assert.Equal(["[announce] Weekly digest"], await SubjectsAsync("tag:auto.news", ct));
        Assert.Equal(["Flash sale: 20% off everything"], await SubjectsAsync("tag:auto.marketing", ct));
        Assert.Empty(await SubjectsAsync("tag:auto.social", ct));
        Assert.Single(await SubjectsAsync("-tag:auto.news -tag:auto.marketing", ct));

        var digest = await RunAsync("search", ["tag:auto.news"], ct);
        var id = JsonDocument.Parse(digest.StdOut).RootElement.GetProperty("hits")[0].GetProperty("id").GetInt64();

        var read = await RunAsync("read", [id.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-fetch"], ct);
        using var document = JsonDocument.Parse(read.StdOut);
        Assert.Contains("auto.news", document.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task A_removed_category_stays_removed_and_removing_it_needs_no_server()
    {
        var ct = TestContext.Current.CancellationToken;
        Import(["049-promo-mail.eml"]);
        Assert.Equal(ExitCodes.Ok, (await RunAsync("import-eml", [_workspace.PathTo("eml")], ct)).ExitCode);

        var removed = await RunAsync("tag", ["1", "-auto.marketing", "--local"], ct);
        Assert.Equal(ExitCodes.Ok, removed.ExitCode);
        using (var document = JsonDocument.Parse(removed.StdOut))
            Assert.DoesNotContain("auto.marketing", document.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        Assert.Empty(await SubjectsAsync("tag:auto.marketing", ct));

        var again = await RunAsync("import-eml", [_workspace.PathTo("eml")], ct);
        Assert.Equal(ExitCodes.Ok, again.ExitCode);
        Assert.Empty(await SubjectsAsync("tag:auto.marketing", ct));
    }

    private void Import(string[] names)
    {
        var directory = _workspace.PathTo("eml");
        Directory.CreateDirectory(directory);
        foreach (var name in names) File.WriteAllBytes(Path.Combine(directory, name), Fixtures.Read(name));
    }

    private Task<CliRun> RunAsync(string verb, string[] arguments, CancellationToken ct) =>
        CliRunner.RunAsync(_workspace.DatabasePath, verb, arguments, ct);

    private async Task<List<string>> SubjectsAsync(string query, CancellationToken ct)
    {
        var run = await RunAsync("search", [query, "--order", "date"], ct);
        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        using var document = JsonDocument.Parse(run.StdOut);
        var subjects = new List<string>();
        foreach (var hit in document.RootElement.GetProperty("hits").EnumerateArray())
            subjects.Add(hit.GetProperty("subject").GetString() ?? string.Empty);
        return subjects;
    }
}
