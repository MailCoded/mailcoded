using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Mailcoded.Protocol.Client;

/// <summary>One spawned daemon: framing, id correlation, notifications, and the fatal latch.</summary>
public sealed class DaemonConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly Channel<JsonDocument> _notifications =
        Channel.CreateBounded<JsonDocument>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _stderr = new();
    private readonly Lock _stderrGate = new();
    private readonly Task _reader;
    private readonly Task _errors;

    private int _nextId;
    private int _disposed;
    private volatile string? _faulted;

    private DaemonConnection(Process process)
    {
        _process = process;
        _reader = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
        _errors = Task.Run(() => PumpStderrAsync(_lifetime.Token), CancellationToken.None);
    }

    public ChannelReader<JsonDocument> Notifications => _notifications.Reader;

    public string? FaultReason => _faulted;

    public string Diagnostics
    {
        get { lock (_stderrGate) return _stderr.ToString(); }
    }

    public static DaemonConnection Start(DaemonLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        var process = Process.Start(launch.ToStartInfo())
            ?? throw new DaemonDisconnectedException($"Could not start '{launch.FileName}'.");

        return new DaemonConnection(process);
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        ReadOnlyMemory<byte> parameters,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (_faulted is { } reason) throw new DaemonDisconnectedException(reason);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await SendAsync(id, method, parameters, ct).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);

            using var document = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return Unwrap(document, method);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(int id, string method, ReadOnlyMemory<byte> parameters, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", ProtocolConstants.JsonRpcVersion);
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);

            if (!parameters.IsEmpty)
            {
                writer.WritePropertyName("params");
                using var document = JsonDocument.Parse(parameters);
                document.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        // One writer at a time: two interleaved frames desynchronise the daemon permanently.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_process.StandardInput.BaseStream, buffer.WrittenMemory, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static JsonElement Unwrap(JsonDocument document, string method)
    {
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : -32603;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? method : method;
            string? category = null;
            int? retryAfter = null;
            var requiresUserAction = false;

            if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("category", out var cat)) category = cat.GetString();
                if (data.TryGetProperty("retryAfterMs", out var r) && r.ValueKind == JsonValueKind.Number)
                    retryAfter = r.GetInt32();
                if (data.TryGetProperty("requiresUserAction", out var u) && u.ValueKind is JsonValueKind.True)
                    requiresUserAction = true;
            }

            throw new RpcException(code, message, category, retryAfter, requiresUserAction);
        }

        return root.TryGetProperty("result", out var result)
            ? result.Clone()
            : throw new RpcException(-32603, $"'{method}' returned neither result nor error.", null, null, false);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var stream = _process.StandardOutput.BaseStream;

        while (!ct.IsCancellationRequested)
        {
            ClientFrame frame;
            try
            {
                frame = await FrameCodec.ReadAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                Fault("The daemon pipe failed: " + ex.Message);
                return;
            }

            if (frame.Status == FrameStatus.EndOfStream)
            {
                Fault("The daemon exited.");
                return;
            }

            if (frame.Status == FrameStatus.Malformed)
            {
                Fault("Malformed frame: " + frame.Error);
                return;
            }

            Deliver(frame.Body);
        }
    }

    private void Deliver(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // The daemon answers a bad frame once and then stops reading, so the stream is spent.
            Fault("Unparseable frame from the daemon: " + ex.Message);
            return;
        }

        if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            && _pending.TryRemove(id.GetInt32(), out var completion))
        {
            completion.TrySetResult(document);
            return;
        }

        if (!_notifications.Writer.TryWrite(document)) document.Dispose();
    }

    private void Fault(string reason)
    {
        _faulted ??= reason;
        _notifications.Writer.TryComplete();

        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
                pending.TrySetException(new DaemonDisconnectedException(reason));
        }
    }

    private async Task PumpStderrAsync(CancellationToken ct)
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lock (_stderrGate)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 64 * 1024) _stderr.Remove(0, _stderr.Length - (64 * 1024));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Fault("The connection was disposed.");
        await _lifetime.CancelAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        await Task.WhenAny(Task.WhenAll(_reader, _errors), Task.Delay(2000)).ConfigureAwait(false);

        _process.Dispose();
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}
