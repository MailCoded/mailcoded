using System.Text.RegularExpressions;
using Xunit;

namespace Mailcoded.Core.Tests.Docs;

public sealed class DocumentationClaimsTests
{
    private static readonly string Root = DocumentationTests.LocateRepositoryRoot();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(Root, Path.Combine(parts)));

    [Fact]
    public void The_stated_fixture_count_is_the_number_of_bundled_fixtures()
    {
        var count = Directory.GetFiles(Path.Combine(Root, "fixtures", "eml"), "*.eml").Length;

        Assert.Contains($"all {count} bundled fixtures import", Read("README.md"), StringComparison.Ordinal);
        Assert.Contains($"ships {count} anonymised fixtures", Read("docs", "manual", "10-import-and-sql.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void No_document_blames_aot_for_keeping_bouncycastle_out()
    {
        foreach (var file in new[] { "SPEC.md", "CLAUDE.md" })
        {
            var text = Read(file);
            Assert.DoesNotContain("size gate and the AOT build", text, StringComparison.Ordinal);
            Assert.DoesNotContain("excluded for binary size and AOT", text, StringComparison.Ordinal);
        }

        Assert.Contains("not because of AOT", Read("CLAUDE.md"), StringComparison.Ordinal);
        Assert.Contains("the AOT build is not the reason", Read("SPEC.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void Rpc_doc_says_an_omitted_snippet_or_body_fetch_flag_is_false_on_the_wire()
    {
        var rpc = Regex.Replace(Read("docs", "rpc.md"), @"\s+", " ");

        Assert.Contains("An omitted `includeSnippet` is false on the wire", rpc, StringComparison.Ordinal);
        Assert.Contains("An omitted `fetchIfMissing` is false on the wire", rpc, StringComparison.Ordinal);
        Assert.DoesNotContain("`fetchIfMissing` is true (the default)", rpc, StringComparison.Ordinal);
        Assert.Contains("**Snippets and the body fetch**", rpc, StringComparison.Ordinal);
    }

    [Fact]
    public void Rpc_doc_uses_the_glossary_word_for_a_correspondent_and_lists_the_draft_setting_on_the_account()
    {
        var rpc = Read("docs", "rpc.md");

        Assert.DoesNotContain("long-standing contact", rpc, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"### `AccountDto`\s+`\{[^}]*saveDraftsToServer"), rpc);
    }

    [Fact]
    public void Agents_doc_lists_every_configuration_gate_and_does_not_call_move_unavailable()
    {
        var agents = Read("docs", "agents.md");

        foreach (var variable in new[] { "MAILCODED_ALLOW_TRUST_CHANGE", "MAILCODED_ALLOW_IDENTITY_CHANGE", "MAILCODED_ALLOW_SERVER_DRAFTS", "MAILCODED_ALLOW_MOVE" })
        {
            Assert.Contains(variable, agents, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("| move between folders | **never on this surface**", agents, StringComparison.Ordinal);
        Assert.Contains("MAILCODED_ALLOW_TRUST_CHANGE", Read("docs", "manual", "11-agents.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void Agents_doc_audit_list_names_the_events_the_code_writes()
    {
        var agents = Read("docs", "agents.md");

        foreach (var name in new[]
                 {
                     "cli_unsubscribe", "account_trust_changed", "identity_added", "identity_forgotten", "signature_set",
                     "signature_forgotten", "sender_denied", "server_draft_setting", "server_draft_moved",
                 })
        {
            Assert.Contains($"`{name}`", agents, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("`search` itself is not audited", agents, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_record_keeps_unrun_integration_tests_out_of_the_verified_claims()
    {
        var text = Read("docs", "verification.md");
        var notVerified = text[text.IndexOf("## NOT verified", StringComparison.Ordinal)..];

        Assert.Contains("written but have never run", notVerified, StringComparison.Ordinal);
        Assert.DoesNotContain("1377 passed", text, StringComparison.Ordinal);
    }
}
