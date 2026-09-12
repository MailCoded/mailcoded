using System.Text.RegularExpressions;
using Xunit;

namespace Mailcoded.Core.Tests.Docs;

public sealed class DocumentationTests
{
    private static readonly string Root = LocateRepositoryRoot();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(Root, Path.Combine(parts)));

    /// <summary>"every new method gets a row in docs/rpc.md" was a convention nobody checked.</summary>
    [Fact]
    public void Rpc_doc_has_a_section_for_every_method_the_daemon_serves()
    {
        var doc = Read("docs", "rpc.md");

        foreach (var field in typeof(Mailcoded.Protocol.RpcMethods)
                     .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                     .Where(f => f.IsLiteral))
        {
            var method = (string)field.GetRawConstantValue()!;

            Assert.True(
                doc.Contains($"### `{method}`", StringComparison.Ordinal),
                $"docs/rpc.md has no '### `{method}`' section. Every method needs one in the same change.");
        }
    }

    [Fact]
    public void License_file_exists_and_is_mit_for_the_stated_holder()
    {
        var license = Read("LICENSE");

        Assert.Contains("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2026 Yu Lin (@lywedo)", license, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", license, StringComparison.Ordinal);
        Assert.Contains("WITHOUT WARRANTY OF ANY KIND", license, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_states_the_licence_plainly_without_the_deferral_hedge()
    {
        var readme = Read("README.md");

        Assert.DoesNotContain("lands with the first tagged release", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[LICENSE](LICENSE)", readme, StringComparison.Ordinal);
    }

    /// <summary>DEPENDENCIES is the normative size doc; every other document must quote what it
    /// records for the same binary, and a frozen literal would go stale on the next measurement.</summary>
    [Fact]
    public void Readme_claims_no_single_binary_and_every_doc_quotes_the_measured_sizes()
    {
        var readme = Read("README.md");
        var dependencies = Read("docs/DEPENDENCIES.md");

        foreach (var phrase in new[] { "single-file", "single binary with", "one file" })
            Assert.DoesNotContain(phrase, readme, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("not a single binary", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("libe_sqlite3.so", readme, StringComparison.Ordinal);

        var quoted = new[]
        {
            ("mailcoded-daemon", new[] { "README.md", "docs/manual/01-install.md" }),
            ("mailcoded", new[] { "README.md", "docs/rpc.md", "docs/manual/01-install.md" }),
            ("mailcoded-tui", new[] { "README.md", "docs/rpc.md", "docs/manual/01-install.md" }),
            ("mailcoded-mcp", new[] { "README.md", "docs/manual/01-install.md" }),
        };

        var measured = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (binary, _) in quoted) measured[binary] = MeasuredSize(dependencies, binary);

        foreach (var (binary, quotedBy) in quoted)
        {
            foreach (var document in quotedBy)
                AssertAttributes(Read(document), document, binary, measured[binary], measured.Values);
        }
    }

    /// <summary>The first size *after* the name, not merely one nearby: every document writes
    /// "`name` size", and a window test passes even when two binaries' sizes are swapped.</summary>
    private static void AssertAttributes(
        string document,
        string path,
        string binary,
        string size,
        IReadOnlyCollection<string> everySize)
    {
        const int SameSentence = 160;
        var stated = false;

        foreach (Match mention in Regex.Matches(document, Regex.Escape(binary) + "(?![-\\w])"))
        {
            var nearest = string.Empty;
            var distance = int.MaxValue;
            var after = mention.Index + mention.Length;

            foreach (var candidate in everySize)
            {
                var at = document.IndexOf(candidate, after, StringComparison.Ordinal);
                if (at < 0 || at - after >= distance) continue;
                distance = at - after;
                nearest = candidate;
            }

            if (distance > SameSentence) continue;

            Assert.True(
                string.Equals(nearest, size, StringComparison.Ordinal),
                $"{path} puts {nearest} next to {binary}, which docs/DEPENDENCIES.md measures at {size}.");
            stated = true;
        }

        Assert.True(stated, $"{path} never states {binary}'s measured size of {size}.");
    }

    /// <summary>Newlines are normalized away first: a paragraph reflow must not fail this.</summary>
    private static string MeasuredSize(string dependencies, string binary)
    {
        var flat = Regex.Replace(dependencies, @"\s+", " ");
        var measured = Regex.Match(flat, $@"`{Regex.Escape(binary)}`(?![-\w])[^*]{{0,40}}?\*\*([\d.]+ MiB)\*\*");
        Assert.True(measured.Success, $"docs/DEPENDENCIES.md no longer records a measured size for {binary}.");
        return measured.Groups[1].Value;
    }

    [Fact]
    public void Readme_presents_no_benchmark_number_as_measured()
    {
        var readme = Read("README.md");

        Assert.Contains("These are targets, not measurements", readme, StringComparison.Ordinal);
        Assert.Contains("never been run on reference hardware", readme, StringComparison.Ordinal);
        Assert.Contains("`p95Ns: 0` for every benchmark deliberately", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Architecture_rule_three_bans_implementations_not_all_adapter_references()
    {
        var architecture = Read("docs", "ARCHITECTURE.md");

        Assert.DoesNotContain(
            "Adapters reference `Domain` + their one external library; never each other.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains("**port interface**", architecture, StringComparison.Ordinal);
        Assert.Contains("**implementation**", architecture, StringComparison.Ordinal);
        Assert.Contains("AccountConfig", architecture, StringComparison.Ordinal);
        Assert.Contains("ISecretStore", architecture, StringComparison.Ordinal);
    }

    [Fact]
    public void Architecture_records_that_the_wire_left_the_hexagon()
    {
        var architecture = Read("docs", "ARCHITECTURE.md");
        var section = architecture[architecture.IndexOf("## 12.1", StringComparison.Ordinal)..];

        // Protocol used to be a Core folder depending on Domain/Primitives. It is now its own
        // assembly depending on nothing, which is a stronger claim and the reason a third party
        // can implement docs/rpc.md without taking the engine.
        Assert.Contains("Protocol/", section, StringComparison.Ordinal);
        Assert.Contains("src/Mailcoded.Protocol", section, StringComparison.Ordinal);
        Assert.Contains("BCL alone", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_records_distinguish_the_two_meanings_of_truncated()
    {
        var records = Read("src", "Mailcoded.Core", "Store", "StoreRecords.cs");

        Assert.Contains("always exactly <c>NextCursor is not null</c>", records, StringComparison.Ordinal);
        Assert.Contains("Never means rows were dropped", records, StringComparison.Ordinal);
        Assert.Contains("Matches were DROPPED that no further call can reach", records, StringComparison.Ordinal);
        Assert.DoesNotContain("True when more rows matched than were returned", records, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbox_comment_explains_the_defensive_coalesce()
    {
        var outbox = Read("src", "Mailcoded.Core", "Store", "SqliteStore.Outbox.cs");

        Assert.DoesNotContain("carries no envelope", outbox, StringComparison.Ordinal);
        Assert.Contains("cannot erase the stored Bcc", outbox, StringComparison.Ordinal);
    }

    [Fact]
    public void Rpc_doc_separates_paging_truncation_from_lost_matches()
    {
        var rpc = Read("docs", "rpc.md");

        Assert.Contains("The two meanings of `truncated`", rpc, StringComparison.Ordinal);
        Assert.Contains("keep paging, nothing was lost", rpc, StringComparison.Ordinal);
        Assert.Contains("**with a null cursor**", rpc, StringComparison.Ordinal);
    }

    [Fact]
    public void Rpc_doc_records_the_cli_and_mcp_divergences()
    {
        var rpc = Read("docs", "rpc.md");
        var section = rpc[rpc.IndexOf("### 7.2 Surface divergences", StringComparison.Ordinal)..];

        Assert.Contains("`1..1000`, default **200**", section, StringComparison.Ordinal);
        Assert.Contains("`1..500`, default **200**", section, StringComparison.Ordinal);
        Assert.Contains("`1..2000`, default **500**", section, StringComparison.Ordinal);
        Assert.Contains("1,048,576 bytes", section, StringComparison.Ordinal);
        Assert.Contains("no body-size limit at all", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Skill_tells_the_agent_that_a_cursor_means_keep_paging()
    {
        var skill = Read("SKILL.md");

        Assert.Contains("there is another page, and nothing was", skill, StringComparison.Ordinal);
        Assert.Contains("Keep going until `truncated` is", skill, StringComparison.Ordinal);
        Assert.Contains("the MCP `thread` tool caps", skill, StringComparison.Ordinal);
        Assert.Contains("the MCP `draft` tool enforces no body size at all", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_draft_directory_exists_with_its_human_posts_rule()
    {
        Assert.True(File.Exists(Path.Combine(Root, "docs", "launch", ".gitkeep")));

        var readme = Read("docs", "launch", "README.md");
        Assert.Contains("A HUMAN POSTS", readme, StringComparison.Ordinal);
        Assert.Contains("only drafts", readme, StringComparison.Ordinal);
    }

    internal static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mailcoded.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No 'Mailcoded.slnx' above '{AppContext.BaseDirectory}'.");
    }
}
