using System.Text.RegularExpressions;
using Mailcoded.Cli.Commands;
using Mailcoded.Core.Domain.Search;
using Xunit;

namespace Mailcoded.Core.Tests.Docs;

public sealed class ReferenceCompletenessTests
{
    private static readonly string Root = DocumentationTests.LocateRepositoryRoot();

    private static readonly string[] GlobalOptions = ["--json", "--help", "--quiet", "--data-dir", "--db"];

    private static readonly string[] HostInternalVariables = ["MAILCODED_TRANSCRIPT", "MAILCODED_PARENT_PID"];

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(Root, Path.Combine(parts)));

    private static string Reference() => Read("docs", "manual", "14-reference.md");

    private static IEnumerable<string> TableVerbs() =>
        CommandTable.Names().Split(", ").Where(name => CommandTable.Find(name) is not null);

    [Fact]
    public void The_reference_lists_every_verb_in_the_command_table()
    {
        var reference = Reference();

        foreach (var verb in CommandTable.Names().Split(", "))
        {
            Assert.True(
                reference.Contains($"`{verb}", StringComparison.Ordinal),
                $"docs/manual/14-reference.md does not mention the verb '{verb}'.");
        }
    }

    [Fact]
    public void The_reference_lists_every_option_each_verb_accepts()
    {
        var reference = Reference();
        var section = reference[reference.IndexOf("## Options by verb", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", 5, StringComparison.Ordinal)];

        foreach (var verb in TableVerbs())
        {
            var row = section.Split('\n').FirstOrDefault(line => line.StartsWith($"| `{verb}` |", StringComparison.Ordinal));
            Assert.True(row is not null, $"The options table has no row for '{verb}'.");

            var accepted = CommandTable.Find(verb)!.Spec.Accepted()
                .Split(", ")
                .Select(option => option.Split(' ')[0])
                .Where(option => !GlobalOptions.Contains(option))
                .ToArray();

            foreach (var option in accepted)
            {
                Assert.True(
                    row!.Contains($"`{option}`", StringComparison.Ordinal),
                    $"The options row for '{verb}' does not list {option}.");
            }

            if (accepted.Length == 0) Assert.Contains("none", row!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_reference_lists_every_environment_variable_the_hosts_read()
    {
        var reference = Reference();
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"(MAILCODED_[A-Z_]+)\""))
                names.Add(match.Groups[1].Value);
        }

        Assert.NotEmpty(names);

        foreach (var name in names.Except(HostInternalVariables))
        {
            Assert.True(
                reference.Contains($"`{name}`", StringComparison.Ordinal),
                $"docs/manual/14-reference.md has no row for {name}.");
        }
    }

    [Fact]
    public void Every_operator_the_reference_documents_parses_without_an_error()
    {
        var reference = Reference();
        var section = reference[reference.IndexOf("## Search operators", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", 5, StringComparison.Ordinal)];

        var operators = Regex.Matches(section, @"`([a-z]+):`").Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.Contains("attachment", operators);
        Assert.Contains("in", operators);
        Assert.Contains("since", operators);

        foreach (var op in operators)
        {
            var value = op is "before" or "after" or "since" ? "2026-01-31" : "example";
            Assert.False(SearchQueryParser.Parse($"{op}:{value}").HasErrors, $"The documented operator '{op}:' does not parse.");
        }

        foreach (Match match in Regex.Matches(section, @"`is:([a-z]+)`"))
        {
            var query = $"is:{match.Groups[1].Value}";
            Assert.False(SearchQueryParser.Parse(query).HasErrors, $"The documented '{query}' does not parse.");
        }

        Assert.False(SearchQueryParser.Parse("in:anywhere").HasErrors);
    }

    [Fact]
    public void The_manual_summary_lists_every_chapter()
    {
        var summary = Read("docs", "manual", "SUMMARY.md");
        var chapters = Directory.GetFiles(Path.Combine(Root, "docs", "manual"), "*.md")
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^\d\d-"))
            .ToArray();

        Assert.NotEmpty(chapters);

        foreach (var chapter in chapters.Select(Path.GetFileName))
            Assert.Contains($"({chapter})", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_documentation_table_links_the_agent_guide()
    {
        Assert.Contains("(docs/agents.md)", Read("README.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_spec_records_the_added_milestone_and_that_the_gmail_rest_provider_was_not_built()
    {
        var spec = Regex.Replace(Read("SPEC.md"), @"\s+", " ");

        Assert.Contains("**M2a — Owner-added feature set**", spec, StringComparison.Ordinal);
        Assert.Contains("added by the owner's decision", spec, StringComparison.Ordinal);
        Assert.Contains("native Gmail REST provider was proposed with the M2a set and **not built**", spec, StringComparison.Ordinal);
        Assert.Contains("allows only mail protocols and the Graph/MSAL endpoints", spec, StringComparison.Ordinal);
    }
}
