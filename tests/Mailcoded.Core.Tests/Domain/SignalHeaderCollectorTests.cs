using Mailcoded.Core.Domain.Signals;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class SignalHeaderCollectorTests
{
    [Fact]
    public void The_fetch_list_is_exactly_the_nine_scoped_headers()
    {
        Assert.Equal(
            new[]
            {
                "List-Id", "List-Unsubscribe", "List-Unsubscribe-Post", "Precedence", "Auto-Submitted",
                "X-Autoreply", "X-Autorespond", "X-Failed-Recipients", "Authentication-Results",
            },
            SignalHeaderCollector.FetchNames.ToArray());
    }

    [Theory]
    [InlineData("list-id", true)]
    [InlineData("LIST-UNSUBSCRIBE-POST", true)]
    [InlineData("Authentication-Results", true)]
    [InlineData("Received", false)]
    [InlineData("ARC-Authentication-Results", false)]
    [InlineData("List-Id ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Wants_matches_names_case_insensitively_and_exactly(string? field, bool expected)
    {
        Assert.Equal(expected, SignalHeaderCollector.Wants(field));
    }

    [Fact]
    public void Absent_and_empty_are_different()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("X-Autoreply", "");

        var built = collector.Build();

        Assert.Equal(string.Empty, built.XAutoreply);
        Assert.Null(built.XAutorespond);
        Assert.Empty(built.AuthenticationResults);
    }

    [Fact]
    public void Control_characters_become_spaces_and_the_value_is_trimmed()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("List-Id", "\r\n  a\u0000b\u001bc\t d  ");

        Assert.Equal("a b c  d", collector.Build().ListId);
    }

    [Fact]
    public void An_oversized_value_is_cut_at_the_per_header_cap()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("List-Id", new string('x', 50_000));

        Assert.Equal(SignalHeaderCollector.MaxValueChars, collector.Build().ListId!.Length);
    }

    [Fact]
    public void Duplicates_of_a_single_valued_header_are_joined_so_none_can_hide_another()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("Auto-Submitted", "no");
        collector.Add("auto-submitted", "auto-replied");

        Assert.Equal("no, auto-replied", collector.Build().AutoSubmitted);
    }

    [Fact]
    public void Joining_never_exceeds_the_per_header_cap()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("Precedence", new string('a', SignalHeaderCollector.MaxValueChars));
        collector.Add("Precedence", "bulk");

        Assert.Equal(SignalHeaderCollector.MaxValueChars, collector.Build().Precedence!.Length);
    }

    [Fact]
    public void Authentication_results_keep_message_order_and_stop_at_the_instance_cap()
    {
        var collector = new SignalHeaderCollector();
        for (var i = 0; i < 20; i++) collector.Add("Authentication-Results", $"mx{i}.example.net; dmarc=pass");

        var results = collector.Build().AuthenticationResults;

        Assert.Equal(SignalHeaderCollector.MaxAuthenticationResults, results.Count);
        Assert.StartsWith("mx0.example.net", results[0], StringComparison.Ordinal);
        Assert.StartsWith("mx7.example.net", results[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void One_authentication_result_is_cut_at_its_own_cap()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("Authentication-Results", new string('y', 100_000));

        Assert.Equal(SignalHeaderCollector.MaxAuthenticationResultChars, collector.Build().AuthenticationResults[0].Length);
    }

    [Fact]
    public void The_total_budget_bounds_everything_but_presence_survives_it()
    {
        var collector = new SignalHeaderCollector();
        for (var i = 0; i < SignalHeaderCollector.MaxAuthenticationResults; i++)
            collector.Add("Authentication-Results", new string('z', SignalHeaderCollector.MaxAuthenticationResultChars));

        collector.Add("List-Id", new string('a', 1_000));
        collector.Add("X-Autoreply", "yes");

        var built = collector.Build();
        var total = built.AuthenticationResults.Sum(r => r.Length) + (built.ListId?.Length ?? 0) + (built.XAutoreply?.Length ?? 0);

        Assert.True(total <= SignalHeaderCollector.MaxTotalChars, $"kept {total} chars");
        Assert.NotNull(built.ListId);
        Assert.NotNull(built.XAutoreply);
    }

    [Fact]
    public void Unknown_headers_are_ignored()
    {
        var collector = new SignalHeaderCollector();
        collector.Add("Subject", "hello");
        collector.Add(null, "x");

        Assert.Equal(SignalHeaders.None.AuthenticationResults.Count, collector.Build().AuthenticationResults.Count);
        Assert.Null(collector.Build().ListId);
    }
}
