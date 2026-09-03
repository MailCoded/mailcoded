using System.Collections.Concurrent;
using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Protocol;

namespace Mailcoded.Daemon;

/// <summary>
/// The stdio read loop. It frames, parses, dispatches, and answers; it never writes to stdout
/// directly — everything outbound goes through <see cref="RpcChannel"/> so frames cannot interleave.
/// </summary>
internal sealed class JsonRpcServer
{
    private readonly FrameReader reader;
    private readonly RpcChannel channel;
    private readonly RpcDispatcher dispatcher;
    private readonly StderrLog log;
    private readonly TaskCompletionSource shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, Task> inFlight = new();
    private long ticket;

    public JsonRpcServer(FrameReader reader, RpcChannel channel, RpcDispatcher dispatcher, StderrLog log)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);

        this.reader = reader;
        this.channel = channel;
        this.dispatcher = dispatcher;
        this.log = log;
    }

    /// <summary>Completes once the client called <c>shutdown</c> and its response was queued.</summary>
    public Task ShutdownRequested => shutdown.Task;

    /// <summary>
    /// Returns on stdin EOF, on a fatal framing error, or on cancellation.
    /// <paramref name="readCt"/> stops the loop accepting new work; <paramref name="handlerCt"/>
    /// is what a running handler observes. They are separate so shutdown can stop reading and
    /// still let in-flight requests produce their responses.
    /// </summary>
    public async Task RunAsync(CancellationToken readCt, CancellationToken handlerCt)
    {
        var ct = readCt;
        while (!ct.IsCancellationRequested)
        {
            JsonRpcRequest? request;

            using (var frame = await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (frame.Status == FrameStatus.EndOfStream)
                {
                    log.Info("stdin reached EOF; shutting down.");
                    return;
                }

                if (frame.Status == FrameStatus.Malformed)
                {
                    // Framing is a stream property: once it is wrong the rest of the pipe is
                    // unparseable, so answer once and stop rather than resynchronizing on garbage.
                    log.Warn($"Malformed frame: {frame.Error}");
                    channel.TryEnqueue(RpcPayloads.Error(
                        null,
                        RpcErrorMapper.Build(RpcErrorCode.ParseError, frame.Error ?? "The frame was malformed.", "protocol")));
                    return;
                }

                request = TryParse(frame.Span);
            }

            if (request is null) continue;

            Begin(request, handlerCt);
        }
    }

    /// <summary>Waits for handlers that are already running, so a queued response is not lost.</summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pending = inFlight.Values.ToArray();
        if (pending.Length == 0) return;

        try
        {
            await Task.WhenAll(pending).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            log.Warn($"{pending.Length} request handler(s) did not finish before the shutdown deadline.");
        }
    }

    private JsonRpcRequest? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            var request = JsonSerializer.Deserialize(body, ProtocolJsonContext.Default.JsonRpcRequest);
            if (request is null || string.IsNullOrEmpty(request.Method))
            {
                channel.TryEnqueue(RpcPayloads.Error(
                    null,
                    RpcErrorMapper.Build(RpcErrorCode.InvalidRequest, "The payload was not a JSON-RPC request object.", "protocol")));
                return null;
            }

            if (!string.Equals(request.JsonRpc, ProtocolConstants.JsonRpcVersion, StringComparison.Ordinal))
            {
                channel.TryEnqueue(RpcPayloads.Error(
                    request.Id,
                    RpcErrorMapper.Build(RpcErrorCode.InvalidRequest, "Only JSON-RPC 2.0 is supported.", "protocol")));
                return null;
            }

            return request;
        }
        catch (JsonException ex)
        {
            log.Debug($"Unparseable request body: {AuditText.Sanitize(ex.Message, 200)}");
            channel.TryEnqueue(RpcPayloads.Error(
                null,
                RpcErrorMapper.Build(RpcErrorCode.ParseError, "The request body was not valid JSON.", "protocol")));
            return null;
        }
        catch (NotSupportedException)
        {
            channel.TryEnqueue(RpcPayloads.Error(
                null,
                RpcErrorMapper.Build(RpcErrorCode.InvalidRequest, "The payload was not a JSON-RPC request object.", "protocol")));
            return null;
        }
    }

    /// <summary>
    /// Handlers run off the read loop so a minutes-long <c>sync</c> cannot stall <c>shutdown</c>.
    /// Concurrency is safe: the store serializes writes and each provider serializes its commands.
    /// </summary>
    private void Begin(JsonRpcRequest request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref ticket);
        var work = Task.Run(() => HandleAsync(request, ct), CancellationToken.None);

        inFlight[id] = work;
        _ = work.ContinueWith(
            _ => inFlight.TryRemove(id, out Task? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleAsync(JsonRpcRequest request, CancellationToken ct)
    {
        byte[]? payload = null;

        try
        {
            var result = await dispatcher.DispatchAsync(request.Method, request.Params, ct).ConfigureAwait(false);
            if (!request.IsNotification) payload = RpcPayloads.Result(request.Id, result);
        }
        catch (Exception ex)
        {
            var error = RpcErrorMapper.Map(ex, log);
            if (!request.IsNotification) payload = RpcPayloads.Error(request.Id, error);
            else log.Warn($"Notification '{AuditText.Sanitize(request.Method, 48)}' failed with code {error.Code}.");
        }

        if (payload is not null) channel.TryEnqueue(payload);

        // The response is queued before the signal, so shutdown always answers before the pipe closes.
        if (dispatcher.ShutdownRequested) shutdown.TrySetResult();
    }
}
