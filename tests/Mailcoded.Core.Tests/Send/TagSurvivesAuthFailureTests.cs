using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>CLAUDE invariant 9: local wins on custom tags. A dead credential is as unreachable as
/// a dead network, so neither may cost the local write.</summary>
public sealed class TagSurvivesAuthFailureTests
{
    [Fact]
    public void The_cli_defers_the_push_on_any_provider_failure_including_auth()
    {
        var source = File.ReadAllText(RepositoryFile("src/Mailcoded.Cli/Commands/TagCommand.cs"));

        Assert.DoesNotContain(
            "when (ex.Category is not FailureCategory.Auth)",
            source,
            StringComparison.Ordinal);

        Assert.Contains("catch (ProviderException ex)", source, StringComparison.Ordinal);
    }

    /// <summary>The three surfaces disagreed: two kept the tag, one threw it away.</summary>
    [Theory]
    [InlineData("src/Mailcoded.Daemon/RpcDispatcher.cs")]
    [InlineData("src/Mailcoded.Mcp/MailTools.cs")]
    [InlineData("src/Mailcoded.Cli/Commands/TagCommand.cs")]
    public void No_tag_surface_rethrows_a_provider_failure_before_writing_locally(string file)
    {
        var source = File.ReadAllText(RepositoryFile(file));

        Assert.DoesNotContain(
            "is not FailureCategory.Auth",
            source,
            StringComparison.Ordinal);
    }

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"No '{relative}' above '{AppContext.BaseDirectory}'.");
    }
}
