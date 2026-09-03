using System.Net;
using System.Net.Sockets;

namespace Mailcoded.Core.Providers;

/// <summary>Connects over whichever address family answers (RFC 8305). .NET works through the
/// resolver's order and never falls back, so an AAAA with no IPv6 route stalls until a timeout.</summary>
public static class DualStackConnector
{
    /// <summary>Head start for the first family before the next is raced (RFC 8305 §5).</summary>
    public static readonly TimeSpan FallbackDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<Socket> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero) deadline.CancelAfter(timeout);

        IPAddress[] resolved;
        try
        {
            resolved = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, deadline.Token).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new ProviderException(FailureCategory.Network, $"Could not resolve {host}: {ex.Message}", ex);
        }

        var order = Interleave(resolved);
        if (order.Count == 0)
            throw new ProviderException(FailureCategory.Network, $"{host} resolved to no usable address.");

        return await RaceAsync(order, host, port, deadline.Token, ct).ConfigureAwait(false);
    }

    /// <summary>Alternates families so neither can monopolise the attempt order the resolver chose.</summary>
    public static IReadOnlyList<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var sixes = new Queue<IPAddress>();
        var fours = new Queue<IPAddress>();

        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6) sixes.Enqueue(address);
            else if (address.AddressFamily == AddressFamily.InterNetwork) fours.Enqueue(address);
        }

        var order = new List<IPAddress>(sixes.Count + fours.Count);
        while (sixes.Count > 0 || fours.Count > 0)
        {
            if (sixes.Count > 0) order.Add(sixes.Dequeue());
            if (fours.Count > 0) order.Add(fours.Dequeue());
        }

        return order;
    }

    private static async Task<Socket> RaceAsync(
        IReadOnlyList<IPAddress> order,
        string host,
        int port,
        CancellationToken deadline,
        CancellationToken ct)
    {
        using var won = CancellationTokenSource.CreateLinkedTokenSource(deadline);

        var attempts = new List<Task<Socket>>(order.Count);
        Exception? last = null;

        for (var i = 0; i < order.Count; i++)
        {
            attempts.Add(AttemptAsync(order[i], port, FallbackDelay * i, won.Token));
        }

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);

            if (finished.IsCompletedSuccessfully)
            {
                await won.CancelAsync().ConfigureAwait(false);
                Discard(attempts);
                return finished.Result;
            }

            if (finished.Exception?.GetBaseException() is { } failure and not OperationCanceledException)
                last = failure;
        }

        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

        var why = $"Could not reach {host}:{port} on any address. {last?.Message ?? "The attempts timed out."}";
        throw last is null
            ? new ProviderException(FailureCategory.Network, why)
            : new ProviderException(FailureCategory.Network, why, last);
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, TimeSpan stagger, CancellationToken ct)
    {
        if (stagger > TimeSpan.Zero) await Task.Delay(stagger, ct).ConfigureAwait(false);

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A loser that connects after the race is over still holds a socket on the server.</summary>
    private static void Discard(IReadOnlyList<Task<Socket>> attempts)
    {
        foreach (var attempt in attempts)
        {
            _ = attempt.ContinueWith(
                t => t.Result.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
