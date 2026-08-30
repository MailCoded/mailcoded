using System.IO.Pipelines;
using System.Text;
using Mailcoded.Daemon;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class FramingTests
{
    private const string Body = """{"jsonrpc":"2.0","id":1,"method":"health"}""";

    [Fact]
    public async Task ReadAsync_ReadsAWellFormedFrame()
    {
        var reader = ReaderOver(Frame(Body));

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Message, frame.Status);
        Assert.Null(frame.Error);
        Assert.Equal(Encoding.UTF8.GetByteCount(Body), frame.Length);
        Assert.Equal(Body, Encoding.UTF8.GetString(frame.Span));
    }

    [Fact]
    public async Task ReadAsync_ReassemblesAFrameSplitAcrossBufferBoundaries()
    {
        var reader = ReaderOver(Frame(Body), chunkSize: 1);

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Message, frame.Status);
        Assert.Equal(Body, Encoding.UTF8.GetString(frame.Span));
    }

    [Fact]
    public async Task ReadAsync_ReadsMultipleFramesBackToBack()
    {
        const string second = """{"jsonrpc":"2.0","id":2,"method":"stats"}""";
        var payload = Concat(Frame(Body), Frame(second));
        var reader = ReaderOver(payload);
        var ct = TestContext.Current.CancellationToken;

        using (var first = await reader.ReadAsync(ct))
        {
            Assert.Equal(FrameStatus.Message, first.Status);
            Assert.Equal(Body, Encoding.UTF8.GetString(first.Span));
        }

        using (var next = await reader.ReadAsync(ct))
        {
            Assert.Equal(FrameStatus.Message, next.Status);
            Assert.Equal(second, Encoding.UTF8.GetString(next.Span));
        }

        using var end = await reader.ReadAsync(ct);
        Assert.Equal(FrameStatus.EndOfStream, end.Status);
    }

    [Fact]
    public async Task ReadAsync_AcceptsExtraHeadersBeforeTheBlankLine()
    {
        var header =
            "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n"
            + "X-Vendor: mailcoded\r\n"
            + $"CONTENT-LENGTH: {Encoding.UTF8.GetByteCount(Body)}\r\n\r\n";

        var reader = ReaderOver(Concat(Encoding.ASCII.GetBytes(header), Encoding.UTF8.GetBytes(Body)));

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Message, frame.Status);
        Assert.Equal(Body, Encoding.UTF8.GetString(frame.Span));
    }

    [Fact]
    public async Task ReadAsync_RejectsAHeaderWithoutContentLength()
    {
        var reader = ReaderOver(Encoding.ASCII.GetBytes("X-Vendor: mailcoded\r\n\r\n" + Body));

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Malformed, frame.Status);
        Assert.Contains("Content-Length", frame.Error, StringComparison.Ordinal);
        Assert.Equal(0, frame.Length);
    }

    [Theory]
    [InlineData("Content-Length: abc\r\n\r\n")]
    [InlineData("Content-Length: -12\r\n\r\n")]
    [InlineData("Content-Length: 12x\r\n\r\n")]
    [InlineData("Content-Length: \r\n\r\n")]
    public async Task ReadAsync_RejectsAMalformedContentLength(string header)
    {
        var reader = ReaderOver(Encoding.ASCII.GetBytes(header + Body));

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Malformed, frame.Status);
        Assert.Equal(0, frame.Length);
        Assert.NotNull(frame.Error);
    }

    [Fact]
    public async Task ReadAsync_RejectsAContentLengthAboveTheCap()
    {
        var reader = ReaderOver(
            Encoding.ASCII.GetBytes("Content-Length: 1500000000\r\n\r\n" + Body),
            maxContentLength: 4096);

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Malformed, frame.Status);
        Assert.Equal(0, frame.Length);
        Assert.Equal(0, frame.Span.Length);
        Assert.Contains("4096", frame.Error, StringComparison.Ordinal);
    }

    /// <summary>A hostile Content-Length must be refused before a buffer of that size is rented.</summary>
    [Fact]
    public void ReadAsync_DoesNotAllocateWhatAHostileContentLengthAsksFor()
    {
        const long hostile = 1_500_000_000;

        var status = FrameStatus.Message;
        var allocated = 0L;

        var worker = new Thread(() =>
        {
            var reader = ReaderOver(
                Encoding.ASCII.GetBytes($"Content-Length: {hostile}\r\n\r\n" + Body),
                maxContentLength: 4096);

            var before = GC.GetAllocatedBytesForCurrentThread();
            using var frame = reader.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            status = frame.Status;
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the frame reader did not answer within 30s");

        Assert.Equal(FrameStatus.Malformed, status);
        Assert.InRange(allocated, 0, 1_000_000);
    }

    [Fact]
    public async Task ReadAsync_RejectsAHeaderThatNeverTerminates()
    {
        var noise = new string('x', 16 * 1024);
        var reader = ReaderOver(Encoding.ASCII.GetBytes("Content-Length: 10\r\nX-Pad: " + noise));

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Malformed, frame.Status);
    }

    [Fact]
    public async Task ReadAsync_RejectsABodyThatEndsEarly()
    {
        var reader = ReaderOver(Encoding.ASCII.GetBytes("Content-Length: 400\r\n\r\n" + Body));

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.Malformed, frame.Status);
        Assert.Contains("body", frame.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_ReportsEndOfStreamOnAnEmptyPipe()
    {
        var reader = ReaderOver([]);

        using var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FrameStatus.EndOfStream, frame.Status);
    }

    [Fact]
    public async Task WriteAsync_EmitsTheContentLengthHeaderAndTheExactBody()
    {
        var stream = new MemoryStream();
        var writer = new FrameWriter(stream);
        var body = Encoding.UTF8.GetBytes("""{"ok":true}""");

        await writer.WriteAsync(body, TestContext.Current.CancellationToken);

        Assert.Equal("Content-Length: 11\r\n\r\n{\"ok\":true}", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsThroughTheReader()
    {
        var stream = new MemoryStream();
        var writer = new FrameWriter(stream);
        var ct = TestContext.Current.CancellationToken;

        await writer.WriteAsync(Encoding.UTF8.GetBytes(Body), ct);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("""{"id":2}"""), ct);

        var reader = ReaderOver(stream.ToArray());

        using (var first = await reader.ReadAsync(ct)) Assert.Equal(Body, Encoding.UTF8.GetString(first.Span));
        using (var second = await reader.ReadAsync(ct)) Assert.Equal("""{"id":2}""", Encoding.UTF8.GetString(second.Span));
    }

    [Fact]
    public async Task WriteAsync_InUnframedModeTerminatesWithANewline()
    {
        var stream = new MemoryStream();
        var writer = new FrameWriter(stream, framed: false);

        await writer.WriteAsync(Encoding.UTF8.GetBytes("""{"ok":true}"""), TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\":true}\n", Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static FrameReader ReaderOver(byte[] payload, int chunkSize = 0, int maxContentLength = 32 * 1024 * 1024)
    {
        Stream source = chunkSize > 0 ? new DribbleStream(payload, chunkSize) : new MemoryStream(payload, writable: false);
        return new FrameReader(PipeReader.Create(source), maxContentLength);
    }

    private static byte[] Frame(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return Concat(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), bytes);
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var buffer = new byte[first.Length + second.Length];
        first.CopyTo(buffer, 0);
        second.CopyTo(buffer, first.Length);
        return buffer;
    }

    /// <summary>Hands out at most <c>chunk</c> bytes per read so every frame straddles a buffer edge.</summary>
    private sealed class DribbleStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _position;

        public DribbleStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _position);
            if (take <= 0) return 0;

            _data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
