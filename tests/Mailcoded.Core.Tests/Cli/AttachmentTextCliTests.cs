using System.Text.Json;
using Mailcoded.Cli;
using Mailcoded.Core.Application;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class AttachmentTextCliTests : IDisposable
{
    private readonly SurfaceWorkspace _workspace = new("cli-attachment-text");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Search_names_the_attachment_a_hit_matched_and_plain_words_do_not_reach_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var attachment = await CliRunner.RunAsync(_workspace.DatabasePath, "search", ["attachment:wiring", "--order", "date"], ct);
        Assert.Equal(ExitCodes.Ok, attachment.ExitCode);

        using var document = JsonDocument.Parse(attachment.StdOut);
        var hit = Assert.Single(document.RootElement.GetProperty("hits").EnumerateArray().ToList());
        Assert.Equal("Order confirmation", hit.GetProperty("subject").GetString());

        var match = hit.GetProperty("attachment_match");
        Assert.Equal(1, match.GetProperty("index").GetInt32());
        Assert.Equal("po-4471.txt", match.GetProperty("filename").GetString());
        Assert.Equal("text/plain", match.GetProperty("mime").GetString());
        Assert.Contains("wiring", match.GetProperty("snippet").GetString(), StringComparison.Ordinal);

        var bare = await CliRunner.RunAsync(_workspace.DatabasePath, "search", ["wiring"], ct);
        using var bareDocument = JsonDocument.Parse(bare.StdOut);
        Assert.Equal(0, bareDocument.RootElement.GetProperty("hits").GetArrayLength());
    }

    [Fact]
    public async Task The_human_listing_shows_the_attachment_and_strips_control_characters_from_its_excerpt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct, hostileExcerpt: true);

        var run = await CliRunner.RunAsync(_workspace.DatabasePath, "search", ["attachment:wiring"], ct, json: false);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        Assert.Contains("attachment 1 (po-4471.txt)", run.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001B', run.StdOut);
        Assert.DoesNotContain('‮', run.StdOut);
    }

    private async Task SeedAsync(CancellationToken ct, bool hostileExcerpt = false)
    {
        using var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = _workspace.DatabasePath }, new ManualClock());

        var account = await StoreSeeder.AddAccountAsync(store, ct);
        var inbox = await StoreSeeder.AddInboxAsync(store, account, ct);
        var day = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = await StoreSeeder.AddMessageAsync(
            store, inbox, 1, "a@example.test", "Order confirmation", "Ada <ada@example.test>", "golden@example.test", day, "Your order shipped.", 100, ct);

        var text = hostileExcerpt
            ? "Purchase order: copper wiring \u001B[2J‮evil‬ 200 metres"
            : "Purchase order 4471: copper wiring, 200 metres.";

        await store.SetAttachmentTextsAsync(
            id,
            [new AttachmentTextEntry { Index = 1, MimeType = "text/plain", FileName = "po-4471.txt", Text = text }],
            AttachmentTextIndexing.ExtractorVersion,
            ct);
    }
}
