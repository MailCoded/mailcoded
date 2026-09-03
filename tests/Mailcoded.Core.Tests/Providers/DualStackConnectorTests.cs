using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Mailcoded.Core.Providers;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

/// <summary>Edge case 31 from the inside: .NET does not fall back off a dead IPv6 route by itself.</summary>
public sealed class DualStackConnectorTests
{
    /// <summary>RFC 3849 documentation prefix: routable nowhere, by definition.</summary>
    private static readonly IPAddress Blackhole = IPAddress.Parse("2001:db8::1");

    [Fact]
    public void Interleaving_alternates_families_so_neither_can_monopolise_the_order()
    {
        var order = DualStackConnector.Interleave(
        [
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("2001:db8::2"),
            IPAddress.Parse("2001:db8::3"),
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("10.0.0.2"),
        ]);

        Assert.Equal(
            ["2001:db8::1", "10.0.0.1", "2001:db8::2", "10.0.0.2", "2001:db8::3"],
            order.Select(a => a.ToString()));
    }

    [Fact]
    public void An_address_family_that_is_absent_is_simply_skipped()
    {
        var order = DualStackConnector.Interleave([IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2")]);

        Assert.Equal(["10.0.0.1", "10.0.0.2"], order.Select(a => a.ToString()));
    }

    [Fact]
    public async Task A_reachable_listener_is_connected_to()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = Listen(IPAddress.Loopback);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var socket = await DualStackConnector
            .ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(10), ct);

        Assert.True(socket.Connected);
    }

    /// <summary>The behaviour the whole class exists for: a dead IPv6 must not decide the outcome.</summary>
    [Fact]
    public async Task A_dead_ipv6_address_does_not_stop_a_live_ipv4_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = Listen(IPAddress.Loopback);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var order = DualStackConnector.Interleave([Blackhole, IPAddress.Loopback]);
        Assert.Equal(Blackhole, order[0]);

        var clock = Stopwatch.StartNew();
        using var socket = await Race(order, port, ct);

        Assert.True(socket.Connected);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            $"The IPv4 attempt waited {clock.ElapsedMilliseconds} ms behind a black-holed IPv6 address.");
    }

    [Fact]
    public async Task Nothing_listening_anywhere_reports_the_host_rather_than_hanging()
    {
        var ct = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<ProviderException>(
            () => DualStackConnector.ConnectAsync("127.0.0.1", ClosedPort(), TimeSpan.FromSeconds(5), ct));

        Assert.Equal(FailureCategory.Network, failure.Category);
        Assert.Contains("127.0.0.1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_a_network_failure_not_a_crash()
    {
        var ct = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<ProviderException>(
            () => DualStackConnector.ConnectAsync(
                "no-such-host.invalid", 993, TimeSpan.FromSeconds(10), ct));

        Assert.Equal(FailureCategory.Network, failure.Category);
    }

    /// <summary>Exercises the same race the connector runs, against addresses a test can rely on.</summary>
    private static async Task<Socket> Race(IReadOnlyList<IPAddress> order, int port, CancellationToken ct)
    {
        var method = typeof(DualStackConnector).GetMethod(
            "RaceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("RaceAsync moved; this test races nothing.");

        return await (Task<Socket>)method.Invoke(null, [order, "test", port, ct, ct])!;
    }

    private static Socket Listen(IPAddress address)
    {
        var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(address, 0));
        listener.Listen(8);
        return listener;
    }

    private static int ClosedPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
