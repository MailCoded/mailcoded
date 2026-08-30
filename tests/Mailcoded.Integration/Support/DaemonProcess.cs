using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mailcoded.Integration.Support;

/// <summary>The real daemon over Content-Length framed stdio, so an IDLE assertion runs against the
/// shipped notification path rather than an in-test re-implementation.</summary>
public sealed class DaemonProcess : IAsyncDisposable
{
    private static readonly byte[] HeaderSeparator = "\r\n\r\n"u8.ToArray();

    private readonly Process _process;
    private readonly TestCredentials _credentials;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly ConcurrentQueue<JsonDocument> _notifications = new();
    private readonly StringBuilder _stderr = new();
    private readonly Lock _stderrGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private readonly Task _errors;

    private int _nextId;
    private int _disposed;

    private DaemonProcess(Process process, TestCredentials credentials)
    {
        _process = process;
        _credentials = credentials;
        _reader = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
        _errors = Task.Run(() => PumpStderrAsync(_lifetime.Token), CancellationToken.None);
    }

    public static DaemonProcess Start(DaemonLaunch launch, string storePath, TestCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        var info = new ProcessStartInfo
        {
            FileName = launch.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in launch.Arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--store");
        info.ArgumentList.Add(storePath);
        info.ArgumentList.Add("--log-level");
        info.ArgumentList.Add("debug");

        // A CI box has no unlocked keyring; the file vault is the only backend that can work.
        info.Environment["MAILCODED_SECRET_BACKEND"] = "file";

        var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{launch.FileName}'.");

        return new DaemonProcess(process, credentials);
    }

    public string Diagnostics
    {
        get
        {
            lock (_stderrGate) return _credentials.Redact(_stderr.ToString());
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        Action<Utf8JsonWriter>? parameters,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        await WriteFrameAsync(BuildRequest(id, method, parameters), ct).ConfigureAwait(false);

        JsonDocument response;
        try
        {
            response = await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"'{method}' produced no response within {timeout.TotalSeconds:F0}s.{Environment.NewLine}{Diagnostics}");
        }

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                $"'{method}' failed: {_credentials.Redact(error.GetRawText())}{Environment.NewLine}{Diagnostics}");
        }

        return response.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    /// <summary>Drains queued notifications until one matches, or the budget expires.</summary>
    public async Task<JsonElement?> WaitForNotificationAsync(string method, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            while (_notifications.TryDequeue(out var document))
            {
                if (!document.RootElement.TryGetProperty("method", out var name)) continue;
                if (!string.Equals(name.GetString(), method, StringComparison.Ordinal)) continue;

                return document.RootElement.TryGetProperty("params", out var payload) ? payload.Clone() : default(JsonElement);
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RequestAsync("shutdown", null, TimeSpan.FromSeconds(5), shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A daemon that will not answer is killed below; the process is going away either way.
        }

        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(5_000)) _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_reader, _errors).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _lifetime.Dispose();
        _process.Dispose();
    }

    private static byte[] BuildRequest(int id, string method, Action<Utf8JsonWriter>? parameters)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);

            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                parameters(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private async Task WriteFrameAsync(byte[] payload, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes(
            "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");

        var stream = _process.StandardInput.BaseStream;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var stream = _process.StandardOutput.BaseStream;
        var pending = new List<byte>(8 * 1024);
        var buffer = new byte[8 * 1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;

                for (var i = 0; i < read; i++) pending.Add(buffer[i]);
                DrainFrames(pending);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DrainFrames(List<byte> pending)
    {
        while (true)
        {
            var separator = IndexOfSeparator(pending);
            if (separator < 0) return;

            var header = Encoding.ASCII.GetString(Slice(pending, 0, separator));
            var length = ParseContentLength(header);
            if (length < 0)
            {
                pending.RemoveRange(0, separator + HeaderSeparator.Length);
                continue;
            }

            var bodyStart = separator + HeaderSeparator.Length;
            if (pending.Count - bodyStart < length) return;

            var body = new byte[length];
            pending.CopyTo(bodyStart, body, 0, length);
            pending.RemoveRange(0, bodyStart + length);

            Deliver(body);
        }
    }

    private void Deliver(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return;
        }

        if (document.RootElement.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && _pending.TryRemove(id.GetInt32(), out var completion))
        {
            completion.TrySetResult(document);
            return;
        }

        _notifications.Enqueue(document);
    }

    private async Task PumpStderrAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                lock (_stderrGate)
                {
                    if (_stderr.Length < 64 * 1024) _stderr.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static ReadOnlySpan<byte> Slice(List<byte> source, int start, int length)
    {
        var copy = new byte[length];
        source.CopyTo(start, copy, 0, length);
        return copy;
    }

    private static int IndexOfSeparator(List<byte> pending)
    {
        for (var i = 0; i + HeaderSeparator.Length <= pending.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < HeaderSeparator.Length; j++)
            {
                if (pending[i + j] == HeaderSeparator[j]) continue;
                matched = false;
                break;
            }

            if (matched) return i;
        }

        return -1;
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;

            return int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
        }

        return -1;
    }
}
