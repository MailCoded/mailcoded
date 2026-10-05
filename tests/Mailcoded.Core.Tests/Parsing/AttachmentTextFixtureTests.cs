using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class AttachmentTextFixtureTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    private static ParsedMessage Parse(string fixture) =>
        MessageParser.Default.Parse(Fixtures.Read(fixture), InternalDate, TestContext.Current.CancellationToken);

    [Fact]
    public void A_docx_fixture_yields_body_and_footnote_text_but_not_the_header()
    {
        var parsed = Parse("054-attachment-docx.eml");

        var text = Assert.Single(parsed.AttachmentTexts);
        Assert.Equal("lighting-plan.docx", text.FileName);
        Assert.Contains("Warehouse lighting replacement plan", text.Text, StringComparison.Ordinal);
        Assert.Contains("4 200 dollars & two electricians", text.Text, StringComparison.Ordinal);
        Assert.Contains("项目进度报告已经完成", text.Text, StringComparison.Ordinal);
        Assert.Contains("Quote valid for thirty days", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Confidential", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_type_fixture_reads_csv_latin1_and_an_octet_stream_markdown_but_not_the_pdf()
    {
        var parsed = Parse("055-attachment-text-types.eml");

        Assert.Equal(4, parsed.Attachments.Count);
        Assert.Equal(new[] { "totals.csv", "receipt.txt", "notes.md" }, parsed.AttachmentTexts.Select(t => t.FileName));
        Assert.Contains("north,1200", parsed.AttachmentTexts[0].Text, StringComparison.Ordinal);
        Assert.Equal("Café receipt: crème brûlée", parsed.AttachmentTexts[1].Text);
        Assert.Contains("Scaffolding inspection passed", parsed.AttachmentTexts[2].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(parsed.AttachmentTexts, t => t.Text.Contains("not indexed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_hostile_fixture_costs_warnings_and_leaves_the_plain_attachment_indexed()
    {
        var parsed = Parse("056-attachment-docx-hostile.eml");

        var text = Assert.Single(parsed.AttachmentTexts);
        Assert.Equal("survivor.txt", text.FileName);
        Assert.Equal(4, parsed.Attachments.Count);
        Assert.Contains("attachment-text-skipped:docx-refused", parsed.ParseWarnings);
        Assert.Contains("attachment-text-skipped:docx-compressionratio", parsed.ParseWarnings);
        Assert.Contains("attachment-text-skipped:docx-notazip", parsed.ParseWarnings);
        Assert.DoesNotContain(parsed.AttachmentTexts, t => t.Text.Contains("root:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fixtures_stored_and_searched_end_to_end()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);

        var uid = 1u;
        foreach (var name in new[] { "054-attachment-docx.eml", "055-attachment-text-types.eml", "056-attachment-docx-hostile.eml" })
        {
            var parsed = Parse(name);
            var id = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(uid++, subject: name, hasAttachments: true), ct, bodyText: parsed.BodyText);
            await temp.Store.SetAttachmentTextsAsync(id, AttachmentTextIndexing.EntriesOf(parsed), AttachmentTextIndexing.ExtractorVersion, ct);
        }

        Assert.Equal("054-attachment-docx.eml", Only(temp, "attachment:electricians", ct));
        Assert.Equal("054-attachment-docx.eml", Only(temp, "attachment:项目进度", ct));
        Assert.Equal("055-attachment-text-types.eml", Only(temp, "attachment:scaffolding", ct));
        Assert.Equal("055-attachment-text-types.eml", Only(temp, "attachment:brulee", ct));
        Assert.Equal("056-attachment-docx-hostile.eml", Only(temp, "attachment:beside", ct));
        Assert.Empty(temp.Store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("attachment:passwd").Query }, ct).Hits);
        Assert.Empty(temp.Store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse("attachment:confidential").Query }, ct).Hits);
    }

    private static string Only(TempStore temp, string query, CancellationToken ct)
    {
        var result = temp.Store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse(query).Query }, ct);
        return Assert.Single(result.Hits).Subject ?? string.Empty;
    }
}
