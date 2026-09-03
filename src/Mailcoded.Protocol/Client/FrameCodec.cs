using System.Buffers.Text;
using System.Text;

namespace Mailcoded.Protocol.Client;

public enum FrameStatus
{
    Message,
    EndOfStream,
    Malformed,
}

public readonly record struct ClientFrame(FrameStatus Status, byte[] Body, string? Error)
{
    public static ClientFrame EndOfStream { get; } = new(FrameStatus.EndOfStream, [], null);

    public static ClientFrame Bad(string error) => new(FrameStatus.Malformed, [], error);
}

/// <summary>Client half of the LSP-style framing in docs/rpc.md §1.</summary>
public static class FrameCodec
{
    public const int MaxContentLength = 32 * 1024 * 1024;
    public const int MaxHeaderBytes = 8 * 1024;

    private static ReadOnlySpan<byte> ContentLength => "content-length:"u8;

    public static async Task WriteAsync(Stream destination, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // Length counts BYTES. Measuring the string would desynchronise the daemon permanently
        // on any non-ASCII subject.
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await destination.WriteAsync(header, ct).ConfigureAwait(false);
        await destination.WriteAsync(body, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<ClientFrame> ReadAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        var header = new byte[MaxHeaderBytes];
        var used = 0;
        var single = new byte[1];

        while (true)
        {
            var read = await source.ReadAsync(single.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0) return used == 0 ? ClientFrame.EndOfStream : ClientFrame.Bad("Stream ended mid-header.");

            if (used == MaxHeaderBytes) return ClientFrame.Bad("Header block exceeded 8 KiB.");
            header[used++] = single[0];

            if (used >= 4
                && header[used - 4] == (byte)'\r' && header[used - 3] == (byte)'\n'
                && header[used - 2] == (byte)'\r' && header[used - 1] == (byte)'\n')
            {
                break;
            }
        }

        if (!TryReadContentLength(header.AsSpan(0, used), out var length))
            return ClientFrame.Bad("No usable Content-Length header.");

        // Rejected before allocating, so a hostile header cannot ask for 2 GiB.
        if (length <= 0 || length > MaxContentLength)
            return ClientFrame.Bad($"Content-Length {length} is outside 1..{MaxContentLength}.");

        var body = new byte[length];
        try
        {
            await source.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return ClientFrame.Bad("Stream ended mid-body.");
        }

        return new ClientFrame(FrameStatus.Message, body, null);
    }

    private static bool TryReadContentLength(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;

        for (var i = 0; i < header.Length; i++)
        {
            if (i != 0 && !(header[i - 1] == (byte)'\n')) continue;

            var rest = header[i..];
            if (rest.Length < ContentLength.Length) break;
            if (!Matches(rest[..ContentLength.Length])) continue;

            var value = rest[ContentLength.Length..];
            var end = value.IndexOf((byte)'\r');
            if (end < 0) return false;

            value = value[..end].Trim((byte)' ');
            return Utf8Parser.TryParse(value, out length, out var consumed) && consumed == value.Length;
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<byte> candidate)
    {
        for (var i = 0; i < ContentLength.Length; i++)
        {
            var c = candidate[i];
            if (c >= 'A' && c <= 'Z') c = (byte)(c + 32);
            if (c != ContentLength[i]) return false;
        }

        return true;
    }
}
