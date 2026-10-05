using System.Globalization;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class EncryptedIndexTests
{
    private static string Plan(TempStore temp, string where, CancellationToken ct)
    {
        var result = temp.Store.ExecuteReadOnlyQuery(
            "EXPLAIN QUERY PLAN SELECT m.id FROM messages m WHERE " + where + " ORDER BY m.date_utc DESC, m.id DESC LIMIT 51",
            100,
            ct);

        return string.Join("\n", result.Rows.Select(row => Convert.ToString(row[row.Count - 1], CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Is_encrypted_walks_the_partial_index_instead_of_the_whole_date_index()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var plan = Plan(temp, "m.encrypted = 1", ct);

        Assert.Contains("ix_msg_encrypted", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void The_negated_form_keeps_the_date_index()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        Assert.DoesNotContain("ix_msg_encrypted", Plan(temp, "m.encrypted = 0", ct), StringComparison.Ordinal);
    }
}
