using Mailcoded.Cli;
using Mailcoded.Cli.Commands;
using Mailcoded.Core.Application;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Providers;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class CliArgumentTests
{
    private static VerbSpec Spec(string verb) =>
        (CommandTable.Find(verb) ?? throw new InvalidOperationException($"no verb '{verb}'")).Spec;

    [Fact]
    public void Parse_Search_TakesAQueryPositionalAndItsOptions()
    {
        var line = CommandLine.Parse(
            "search",
            ["tag:unread invoice", "--limit", "20", "--order", "date", "--no-snippet", "--json"],
            Spec("search"));

        Assert.Equal("search", line.Verb);
        Assert.Equal("tag:unread invoice", line.RequirePositional(0, "a query"));
        Assert.Equal(20, line.Int("limit", 1, 200) ?? 0);
        Assert.Equal("date", line.Value("order"));
        Assert.True(line.Flag("no-snippet"));
        Assert.True(line.Flag("json"));
    }

    [Fact]
    public void Parse_AcceptsInlineOptionValues()
    {
        var line = CommandLine.Parse("search", ["q", "--limit=7", "--json=true"], Spec("search"));

        Assert.Equal(7, line.Int("limit", 1, 200) ?? 0);
        Assert.True(line.Flag("json"));
    }

    [Fact]
    public void Parse_TreatsAnExplicitlyFalseFlagAsAbsent()
    {
        var line = CommandLine.Parse("search", ["q", "--json=false"], Spec("search"));
        Assert.False(line.Flag("json"));
    }

    [Fact]
    public void Parse_RejectsANonBooleanFlagValue() =>
        Assert.Throws<CliUsageException>(() => CommandLine.Parse("search", ["q", "--json=maybe"], Spec("search")));

    [Fact]
    public void Parse_RejectsAnOptionTheVerbDoesNotAccept()
    {
        var failure = Assert.Throws<CliUsageException>(
            () => CommandLine.Parse("search", ["q", "--bogus", "1"], Spec("search")));

        Assert.Contains("--bogus", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Accepted:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnOptionMissingItsValue() =>
        Assert.Throws<CliUsageException>(() => CommandLine.Parse("search", ["q", "--limit"], Spec("search")));

    [Fact]
    public void Parse_Tag_KeepsSignedTokensPositional()
    {
        var line = CommandLine.Parse("tag", ["12", "+triaged", "-inbox", "--local"], Spec("tag"));

        Assert.Equal(12L, line.RequireId(0, "a message id"));
        Assert.Equal(new[] { "12", "+triaged", "-inbox" }, line.Positional);
        Assert.True(line.Flag("local"));
    }

    [Fact]
    public void Parse_Draft_CollectsRepeatedRecipientOptions()
    {
        var line = CommandLine.Parse(
            "draft",
            ["--to", "a@example.test", "--to", "b@example.test", "--subject", "Hello", "--body-stdin"],
            Spec("draft"));

        Assert.Equal(new[] { "a@example.test", "b@example.test" }, line.Values("to"));
        Assert.Equal("Hello", line.RequireValue("subject"));
        Assert.True(line.Flag("body-stdin"));
        Assert.Empty(line.Values("cc"));
    }

    [Fact]
    public void Parse_SendDraft_RequiresTheConfirmTokenOption()
    {
        var line = CommandLine.Parse("send-draft", ["7"], Spec("send-draft"));

        Assert.Equal(7L, line.RequireId(0, "a draft id"));
        Assert.Throws<CliUsageException>(() => line.RequireValue("confirm-token"));
    }

    [Fact]
    public void Parse_EveryVerbAcceptsTheGlobalOptions()
    {
        foreach (var verb in CommandTable.Names().Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (CommandTable.Find(verb) is not { } command) continue;

            var line = CommandLine.Parse(
                verb,
                ["--db", "/tmp/store.db", "--data-dir", "/tmp/data", "--json", "--quiet"],
                command.Spec);

            Assert.Equal("/tmp/store.db", line.Value("db"));
            Assert.Equal("/tmp/data", line.Value("data-dir"));
            Assert.True(line.Flag("json"));
            Assert.True(line.Flag("quiet"));
        }
    }

    [Fact]
    public void Parse_DashHRequestsHelpAndDoubleDashEndsOptions()
    {
        Assert.True(CommandLine.Parse("search", ["-h"], Spec("search")).Flag("help"));

        var line = CommandLine.Parse("search", ["--", "--limit"], Spec("search"));
        Assert.Equal(new[] { "--limit" }, line.Positional);
    }

    [Fact]
    public void Parse_RejectsAnOutOfRangeOrNonNumericOption()
    {
        var line = CommandLine.Parse("search", ["q", "--limit", "0"], Spec("search"));
        Assert.Throws<CliUsageException>(() => line.Int("limit", 1, 200));

        var wrong = CommandLine.Parse("search", ["q", "--limit", "many"], Spec("search"));
        Assert.Throws<CliUsageException>(() => wrong.Int("limit", 1, 200));
    }

    [Fact]
    public void Parse_RejectsExtraPositionalsAndNonNumericIds()
    {
        var extra = CommandLine.Parse("search", ["one", "two"], Spec("search"));
        Assert.Throws<CliUsageException>(() => extra.RejectExtraPositional(1));

        var bad = CommandLine.Parse("read", ["nine"], Spec("read"));
        Assert.Throws<CliUsageException>(() => bad.RequireId(0, "a message id"));

        var negative = CommandLine.Parse("read", ["-1"], Spec("read"));
        Assert.Throws<CliUsageException>(() => negative.RequireId(0, "a message id"));
    }

    [Fact]
    public void ExitCodes_AreDistinctForEveryClassAnAgentBranchesOn()
    {
        var codes = new[]
        {
            ExitCodes.Ok, ExitCodes.Internal, ExitCodes.Validation, ExitCodes.NotFound,
            ExitCodes.Forbidden, ExitCodes.RateLimited, ExitCodes.ConfirmRequired,
            ExitCodes.Auth, ExitCodes.Network, ExitCodes.Store, ExitCodes.Unsupported,
        };

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, ExitCodes.Ok);
        Assert.Equal(2, ExitCodes.Validation);
        Assert.Equal(3, ExitCodes.NotFound);
        Assert.Equal(4, ExitCodes.Forbidden);
        Assert.Equal(5, ExitCodes.RateLimited);
    }

    [Fact]
    public void CliError_MapsEveryFailureClassToItsExitCodeAndRpcCode()
    {
        Assert.Equal(ExitCodes.Validation, CliError.From(new CliUsageException("bad flag")).ExitCode);
        Assert.Equal(ExitCodes.Validation, CliError.From(new ArgumentException("bad param")).ExitCode);

        Assert.Equal(
            ExitCodes.NotFound,
            CliError.From(new StoreException(FailureCategory.NotFound, "gone")).ExitCode);

        var forbidden = CliError.From(new PolicyDeniedException(PolicyDenialReason.SendDisabled, "off"));
        Assert.Equal(ExitCodes.Forbidden, forbidden.ExitCode);
        Assert.Equal(RpcErrorCode.Forbidden, forbidden.Code);
        Assert.NotNull(forbidden.Hint);

        var limited = CliError.From(
            new PolicyDeniedException(PolicyDenialReason.RateLimited, "spent", TimeSpan.FromMinutes(2)));
        Assert.Equal(ExitCodes.RateLimited, limited.ExitCode);
        Assert.Equal(RpcErrorCode.RateLimited, limited.Code);
        Assert.Equal(120_000L, limited.RetryAfterMs ?? 0L);

        var confirm = CliError.From(new ConfirmRequiredException("token required"));
        Assert.Equal(ExitCodes.ConfirmRequired, confirm.ExitCode);
        Assert.Equal(RpcErrorCode.ConfirmRequired, confirm.Code);

        Assert.Equal(
            ExitCodes.Auth,
            CliError.From(new ProviderException(FailureCategory.Auth, "bad login")).ExitCode);

        Assert.Equal(
            ExitCodes.Network,
            CliError.From(new ProviderException(FailureCategory.Network, "unreachable")).ExitCode);

        Assert.Equal(
            ExitCodes.Store,
            CliError.From(new StoreException(FailureCategory.Protocol, "corrupt")).ExitCode);

        Assert.Equal(ExitCodes.Cancelled, CliError.From(new OperationCanceledException()).ExitCode);
    }

    [Fact]
    public void CliError_MessagesAreSingleLineAndCapped()
    {
        var error = CliError.From(new CliUsageException("line one\r\nline two " + new string('x', 2000)));

        Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length <= 480, "the CLI error message was not length-capped");
    }
}
