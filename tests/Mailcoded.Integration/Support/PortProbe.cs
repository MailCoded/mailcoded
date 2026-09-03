using System.Net.Sockets;
using System.Text;

namespace Mailcoded.Integration.Support;

/// <summary>Host-side TCP readiness polling; avoids depending on a container wait strategy.</summary>
public static class PortProbe
{
    /// <summary>Waits for the service, not the socket. Docker's userland proxy accepts a
    /// connection before the container listens and then closes it, which reads as ready and then
    /// fails as "the server unexpectedly disconnected" on the first real command.</summary>
    public static async Task WaitForGreetingAsync(
        string host,
        int port,
        string expectedPrefix,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPrefix);

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        string? last = null;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));

                await client.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);

                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 256, leaveOpen: true);

                var greeting = await reader.ReadLineAsync(attempt.Token).ConfigureAwait(false);
                if (greeting is not null && greeting.StartsWith(expectedPrefix, StringComparison.Ordinal)) return;

                last = greeting is null ? "the connection closed before any greeting" : greeting;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"{host}:{port} did not greet with '{expectedPrefix}' within {timeout.TotalSeconds:F0}s. "
            + $"Last: {last ?? "none"}.");
    }

    public static async Task WaitForOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        Exception? last = null;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            $"{host}:{port} did not accept a connection within {timeout.TotalSeconds:F0}s. Last error: {last?.Message ?? "none"}.");
    }
}
