using Mailcoded.Core.Domain.Sync;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class ConnectionBudgetTests
{
    [Theory]
    [InlineData(8, true, 8, true, 6)]
    [InlineData(8, false, 8, false, 7)]
    [InlineData(4, true, 4, true, 2)]
    [InlineData(3, true, 3, true, 1)]
    [InlineData(2, true, 2, false, 1)]
    [InlineData(1, true, 2, false, 1)]
    [InlineData(0, true, 2, false, 1)]
    [InlineData(-5, false, 2, false, 1)]
    public void The_lane_is_allowed_only_when_a_watcher_still_fits(
        int requested, bool laneEnabled, int total, bool laneAllowed, int watcherSlots)
    {
        var budget = new ConnectionBudget(requested, laneEnabled);

        Assert.Equal(total, budget.Total);
        Assert.Equal(laneAllowed, budget.InteractiveLaneAllowed);
        Assert.Equal(watcherSlots, budget.WatcherSlots);
    }

    [Theory]
    [InlineData(8, 5, 5)]
    [InlineData(8, 9, 6)]
    [InlineData(4, 5, 2)]
    [InlineData(3, 5, 1)]
    [InlineData(2, 5, 1)]
    [InlineData(8, 0, 1)]
    [InlineData(8, -3, 1)]
    public void The_watcher_cap_is_the_tighter_of_the_configured_cap_and_the_budget(
        int total, int configured, int expected)
    {
        Assert.Equal(expected, new ConnectionBudget(total, true).WatcherCap(configured));
    }

    [Fact]
    public void Total_connections_never_exceed_the_budget_for_any_watcher_demand()
    {
        for (var total = 0; total <= 16; total++)
        {
            foreach (var laneEnabled in new[] { true, false })
            {
                var budget = new ConnectionBudget(total, laneEnabled);

                for (var demand = -1; demand <= 20; demand++)
                {
                    var held = 1 + (budget.InteractiveLaneAllowed ? 1 : 0) + budget.WatcherCap(demand);
                    Assert.True(held <= budget.Total, $"total={total} lane={laneEnabled} demand={demand} held={held}");
                }
            }
        }
    }

    [Theory]
    [InlineData(false, 8, 4, true, 8)]
    [InlineData(true, 8, 4, true, 4)]
    [InlineData(true, 3, 4, true, 3)]
    [InlineData(true, 8, 4, false, 4)]
    public void A_latched_low_connection_limit_lowers_the_ceiling(
        bool lowLimit, int max, int lowMax, bool laneEnabled, int expectedTotal)
    {
        var budget = ConnectionBudget.For(lowLimit, max, lowMax, laneEnabled);

        Assert.Equal(expectedTotal, budget.Total);
        Assert.Equal(laneEnabled && expectedTotal >= ConnectionBudget.MinTotalForInteractiveLane, budget.InteractiveLaneAllowed);
    }

    [Fact]
    public void The_default_options_keep_the_whole_account_under_dovecots_default_of_ten()
    {
        var options = Mailcoded.Core.Providers.MailTransportOptions.Default;
        var normal = ConnectionBudget.For(false, options.MaxConnectionsPerAccount, options.LowConnectionMaxConnections, options.InteractiveLane);
        var low = ConnectionBudget.For(true, options.MaxConnectionsPerAccount, options.LowConnectionMaxConnections, options.InteractiveLane);

        Assert.True(normal.Total < 10);
        Assert.True(normal.InteractiveLaneAllowed);
        Assert.True(low.InteractiveLaneAllowed);
        Assert.True(normal.WatcherCap(options.MaxWatchedFolders) >= options.MaxWatchedFolders);
        Assert.True(low.WatcherCap(options.LowConnectionWatchCap) >= options.LowConnectionWatchCap);
    }
}
