using System.Net.Sockets;

namespace Mailcoded.Integration.Support;

/// <summary>Host-side TCP readiness polling; avoids depending on a container wait strategy.</summary>
public static class PortProbe
{
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
