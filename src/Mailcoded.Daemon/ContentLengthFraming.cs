using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.IO.Pipelines;

namespace Mailcoded.Daemon;

internal enum FrameStatus
{
    Message,
    EndOfStream,
    Malformed,
}

/// <summary>
/// One decoded frame. The body lives in a pooled array, so the caller must dispose it and must not
/// hold <see cref="Span"/> across an await.
/// </summary>
internal readonly struct Frame : IDisposable
{
    private readonly byte[]? rented;

    private Frame(FrameStatus status, byte[]? rented, int length, string? error)
    {
        Status = status;
        this.rented = rented;
        Length = length;
        Error = error;
    }

    public FrameStatus Status { get; }

    public int Length { get; }

    public string? Error { get; }

    public ReadOnlySpan<byte> Span => rented is null ? default : rented.AsSpan(0, Length);

    public static Frame Message(byte[] rented, int length) => new(FrameStatus.Message, rented, length, null);

    public static readonly Frame End = new(FrameStatus.EndOfStream, null, 0, null);

    public static Frame Malformed(string error) => new(FrameStatus.Malformed, null, 0, error);

    public void Dispose()
    {
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}

/// <summary>
/// LSP-style <c>Content-Length: N\r\n\r\n&lt;body&gt;</c> reader over a <see cref="PipeReader"/>
/// (RELIABILITY §14.2). A hostile <c>Content-Length</c> is rejected against the cap <em>before</em>
/// a single byte is allocated for it.
/// </summary>
internal sealed class FrameReader
{
    public const int DefaultMaxHeaderBytes = 8 * 1024;
    public const int DefaultMaxContentLength = 32 * 1024 * 1024;

    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;
    private static ReadOnlySpan<byte> CrLf => "\r\n"u8;
    private static ReadOnlySpan<byte> ContentLengthName => "content-length"u8;

    private readonly PipeReader reader;
    private readonly int maxHeaderBytes;
    private readonly int maxContentLength;

    public FrameReader(
        PipeReader reader,
        int maxContentLength = DefaultMaxContentLength,
        int maxHeaderBytes = DefaultMaxHeaderBytes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxContentLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeaderBytes, 32);

        this.reader = reader;
        this.maxContentLength = maxContentLength;
        this.maxHeaderBytes = maxHeaderBytes;
    }

    public async ValueTask<Frame> ReadAsync(CancellationToken ct)
    {
        var header = await ReadHeaderAsync(ct).ConfigureAwait(false);

        if (header.Status == FrameStatus.EndOfStream) return Frame.End;
        if (header.Status == FrameStatus.Malformed)
            return Frame.Malformed(header.Error ?? "The frame header was malformed.");

        return await ReadBodyAsync(header.ContentLength, ct).ConfigureAwait(false);
    }

    private async ValueTask<HeaderResult> ReadHeaderAsync(CancellationToken ct)
    {
        while (true)
        {
            var read = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = read.Buffer;

            var outcome = TryReadHeader(buffer, out var consumed, out var contentLength, out var error);

            if (outcome == HeaderOutcome.Complete)
            {
                reader.AdvanceTo(consumed, consumed);
                return HeaderResult.Ok(contentLength);
            }

            if (outcome == HeaderOutcome.Invalid)
            {
                // The stream is desynchronized past this point; consume nothing and let the caller close.
                reader.AdvanceTo(buffer.Start, buffer.End);
                return HeaderResult.Bad(error ?? "The frame header was malformed.");
            }

            if (buffer.Length > maxHeaderBytes)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                return HeaderResult.Bad($"The frame header exceeded {maxHeaderBytes} bytes without a terminator.");
            }

            if (read.IsCompleted || read.IsCanceled)
            {
                var truncated = buffer.Length > 0;
                reader.AdvanceTo(buffer.Start, buffer.End);
                return truncated
                    ? HeaderResult.Bad("The stream ended inside a frame header.")
                    : HeaderResult.Eof();
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private async ValueTask<Frame> ReadBodyAsync(int contentLength, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(contentLength);
        var filled = 0;

        try
        {
            while (filled < contentLength)
            {
                var read = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = read.Buffer;

                var take = (int)Math.Min(buffer.Length, contentLength - filled);
                if (take > 0)
                {
                    buffer.Slice(0, take).CopyTo(rented.AsSpan(filled, take));
                    filled += take;
                }

                var consumed = buffer.GetPosition(take);
                reader.AdvanceTo(consumed, take > 0 ? consumed : buffer.End);

                if (filled >= contentLength) break;

                if (read.IsCompleted || read.IsCanceled)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    return Frame.Malformed("The stream ended inside a frame body.");
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }

        return Frame.Message(rented, contentLength);
    }

    private HeaderOutcome TryReadHeader(
        in ReadOnlySequence<byte> buffer,
        out SequencePosition consumed,
        out int contentLength,
        out string? error)
    {
        consumed = buffer.Start;
        contentLength = -1;
        error = null;

        var scan = new SequenceReader<byte>(buffer);
        if (!scan.TryReadTo(out ReadOnlySequence<byte> block, HeaderTerminator, advancePastDelimiter: true))
            return HeaderOutcome.NeedMore;

        consumed = scan.Position;

        var lines = new SequenceReader<byte>(block);
        while (!lines.End)
        {
            ReadOnlySequence<byte> line;
            if (!lines.TryReadTo(out line, CrLf, advancePastDelimiter: true))
            {
                line = lines.UnreadSequence;
                lines.Advance(lines.Remaining);
            }

            if (line.Length == 0) continue;
            if (line.Length > maxHeaderBytes)
            {
                error = "A frame header line was too long.";
                return HeaderOutcome.Invalid;
            }

            ReadOnlySpan<byte> span;
            if (line.IsSingleSegment) span = line.FirstSpan;
            else span = line.ToArray();

            var colon = span.IndexOf((byte)':');
            if (colon <= 0) continue;

            if (!EqualsAsciiIgnoreCase(span[..colon].Trim((byte)' '), ContentLengthName)) continue;

            var value = span[(colon + 1)..].Trim((byte)' ').Trim((byte)'\t');
            if (!Utf8Parser.TryParse(value, out int parsed, out var used) || used != value.Length || parsed < 0)
            {
                error = "Content-Length was not a non-negative integer.";
                return HeaderOutcome.Invalid;
            }

            contentLength = parsed;
        }

        if (contentLength < 0)
        {
            error = "The frame header carried no Content-Length.";
            return HeaderOutcome.Invalid;
        }

        if (contentLength == 0)
        {
            error = "Content-Length was zero, which cannot carry a JSON-RPC message.";
            return HeaderOutcome.Invalid;
        }

        if (contentLength > maxContentLength)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"Content-Length {contentLength} exceeds the {maxContentLength} byte cap.");
            return HeaderOutcome.Invalid;
        }

        return HeaderOutcome.Complete;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowercase)
    {
        if (value.Length != lowercase.Length) return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= (byte)'A' and <= (byte)'Z') c = (byte)(c + 32);
            if (c != lowercase[i]) return false;
        }

        return true;
    }

    private enum HeaderOutcome
    {
        NeedMore,
        Complete,
        Invalid,
    }

    private readonly record struct HeaderResult(FrameStatus Status, int ContentLength, string? Error)
    {
        public static HeaderResult Ok(int contentLength) => new(FrameStatus.Message, contentLength, null);
        public static HeaderResult Bad(string error) => new(FrameStatus.Malformed, 0, error);
        public static HeaderResult Eof() => new(FrameStatus.EndOfStream, 0, null);
    }
}

/// <summary>Writes framed payloads to the raw stdout stream. Never touches <c>Console.Out</c>.</summary>
internal sealed class FrameWriter
{
    private static ReadOnlySpan<byte> HeaderPrefix => "Content-Length: "u8;
    private static ReadOnlySpan<byte> HeaderSuffix => "\r\n\r\n"u8;

    private readonly Stream stream;
    private readonly bool framed;

    public FrameWriter(Stream stream, bool framed = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.framed = framed;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (framed)
        {
            var header = ArrayPool<byte>.Shared.Rent(64);
            try
            {
                var offset = 0;
                HeaderPrefix.CopyTo(header.AsSpan(offset));
                offset += HeaderPrefix.Length;

                if (!Utf8Formatter.TryFormat(body.Length, header.AsSpan(offset), out var written))
                    throw new InvalidOperationException("The frame length could not be formatted.");
                offset += written;

                HeaderSuffix.CopyTo(header.AsSpan(offset));
                offset += HeaderSuffix.Length;

                await stream.WriteAsync(header.AsMemory(0, offset), ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }
        }

        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        if (!framed) await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static readonly ReadOnlyMemory<byte> NewLine = new byte[] { (byte)'\n' };
}
