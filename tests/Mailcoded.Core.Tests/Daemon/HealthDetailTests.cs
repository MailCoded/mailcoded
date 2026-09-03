using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

/// <summary>The wire calls this field lastError, and a watcher records the folder it is watching as
/// its detail, so a healthy account reported "INBOX" as the thing that went wrong.</summary>
public sealed class HealthDetailTests
{
    [Fact]
    public void A_healthy_connection_contributes_no_last_error()
    {
        var registry = new ConnectionRegistry(new TestClock());

        registry.Observe(new AccountId(1), ConnectionRole.ImapWatch, ConnectionState.Connected, "INBOX");

        Assert.Null(Detail(registry, ConnectionRole.ImapWatch));
    }

    [Fact]
    public void A_failed_connection_still_reports_why()
    {
        var registry = new ConnectionRegistry(new TestClock());

        registry.Observe(new AccountId(1), ConnectionRole.Imap, ConnectionState.Error, "the server hung up");

        Assert.Equal("the server hung up", Detail(registry, ConnectionRole.Imap));
    }

    private static string? Detail(ConnectionRegistry registry, ConnectionRole role)
    {
        var status = registry.Snapshot().FirstOrDefault(s => s.Role == role);
        return status?.State == ConnectionState.Error ? status.Detail : null;
    }
}
