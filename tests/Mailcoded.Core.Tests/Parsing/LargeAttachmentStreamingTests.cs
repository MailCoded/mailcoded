using System.Text;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

/// <summary>RELIABILITY edge case 14: a 100 MB+ attachment is described, never materialized.</summary>
public sealed class LargeAttachmentStreamingTests
{
    private const long PayloadBytes = 100L * 1024 * 1024;
    private const int ChunkBytes = 1024 * 1024;

    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AHundredMegabyteAttachmentIsEnumeratedFromItsPartHeaders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("huge-attachment");
        var path = workspace.PathFor("huge.eml");
        WriteMessage(path, PayloadBytes);

        long allocated;
        ParsedMessage parsed;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            parsed = MessageParser.Default.Parse(stream, InternalDate, ct);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var attachment = Assert.Single(parsed.Attachments);
        Assert.Equal("application/octet-stream", attachment.MimeType);
        Assert.Equal("huge.bin", attachment.FileName);
        Assert.False(attachment.IsInline);

        Assert.True(
            attachment.Size >= PayloadBytes - 4,
            $"The reported size was {attachment.Size}; it must come from the part, not from a copy.");

        Assert.True(
            allocated < PayloadBytes / 8,
            $"Parsing allocated {allocated} bytes for a {PayloadBytes}-byte attachment; the payload was buffered.");

        Assert.Contains("a small text part", parsed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyingTheAttachmentOutStreamsRatherThanBuffers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("huge-copy");
        var path = workspace.PathFor("huge.eml");
        WriteMessage(path, PayloadBytes);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var sink = new CountingStream();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var copied = MessageParser.Default.CopyAttachmentTo(stream, 0, sink, ct);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var attachment = Require.Ref(copied, "the copied attachment");
        Assert.Equal("huge.bin", attachment.FileName);
        Assert.InRange(sink.BytesWritten, PayloadBytes - 4, PayloadBytes + 4);

        Assert.True(
            allocated < PayloadBytes / 8,
            $"Copying allocated {allocated} bytes for a {PayloadBytes}-byte attachment.");
    }

    [Fact]
    public void AnAttachmentIndexThatDoesNotExistReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = TempWorkspace.Create("huge-index");
        var path = workspace.PathFor("small.eml");
        WriteMessage(path, ChunkBytes);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        Assert.Null(MessageParser.Default.CopyAttachmentTo(stream, 7, Stream.Null, ct));
    }

    private static void WriteMessage(string path, long payloadBytes)
    {
        const string headers =
            "From: big@example.com\r\n"
            + "To: bob@example.org\r\n"
            + "Subject: Huge attachment\r\n"
            + "Date: Tue, 20 Jan 2026 08:00:00 +0000\r\n"
            + "Message-ID: <huge@example.com>\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: multipart/mixed; boundary=\"bndhuge\"\r\n"
            + "\r\n"
            + "--bndhuge\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n"
            + "\r\n"
            + "a small text part\r\n"
            + "--bndhuge\r\n"
            + "Content-Type: application/octet-stream; name=\"huge.bin\"\r\n"
            + "Content-Disposition: attachment; filename=\"huge.bin\"\r\n"
            + "Content-Transfer-Encoding: binary\r\n"
            + "\r\n";

        const string trailer = "\r\n--bndhuge--\r\n";

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes);
        file.Write(Encoding.ASCII.GetBytes(headers));

        var chunk = new byte[ChunkBytes];
        Array.Fill(chunk, (byte)'A');

        for (long written = 0; written < payloadBytes; written += ChunkBytes)
            file.Write(chunk, 0, (int)Math.Min(ChunkBytes, payloadBytes - written));

        file.Write(Encoding.ASCII.GetBytes(trailer));
    }

    private sealed class CountingStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;

        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;

        public override void WriteByte(byte value) => BytesWritten++;
    }
}
