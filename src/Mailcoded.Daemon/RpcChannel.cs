using System.Threading.Channels;

namespace Mailcoded.Daemon;

/// <summary>
/// The single writer for stdout. Responses and notifications are queued here and drained by one
/// pump task, so two concurrent handlers can never interleave bytes inside a frame.
/// </summary>
internal sealed class RpcChannel : IAsyncDisposable
{
    private readonly Channel<byte[]> outbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private readonly FrameWriter writer;
    private readonly StderrLog log;
    private readonly Task pump;
    private int completed;

    public RpcChannel(FrameWriter writer, StderrLog log)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);

        this.writer = writer;
        this.log = log;
        pump = Task.Run(PumpAsync);
    }

    /// <summary>False once the queue is closed; a late notification is dropped, never thrown on.</summary>
    public bool TryEnqueue(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return outbound.Writer.TryWrite(payload);
    }

    /// <summary>Closes the queue and waits for every queued frame to reach stdout.</summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref completed, 1) == 0) outbound.Writer.TryComplete();

        try
        {
            await pump.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log.Warn("The stdout writer did not drain before the shutdown deadline.");
        }
        catch (OperationCanceledException)
        {
            log.Warn("The stdout writer drain was cancelled.");
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await outbound.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (outbound.Reader.TryRead(out var payload))
                    await writer.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            // The client closed the pipe; nothing further can be written and that is a normal exit.
            log.Debug($"stdout closed: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            log.Debug("stdout was disposed while draining.");
        }
        catch (Exception ex)
        {
            log.Exception("The stdout writer failed.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0) outbound.Writer.TryComplete();

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // PumpAsync already reported everything it could.
        }
    }
}
