using System.Text;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

/// <summary>RELIABILITY §14.1 budgets active-sync RSS at 150 MB, so the part count has to be bounded
/// before MimeKit materializes the tree rather than after.</summary>
public sealed class MimePartBombTests
{
    private const int BombParts = 200_000;
    private const long AllocationCeiling = 4L * 1024 * 1024;

    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APartCountBombIsRefusedInBoundedMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var raw = Bomb(BombParts);

        MimeStructureLimitException? rejected = null;
        var before = GC.GetAllocatedBytesForCurrentThread();

        try
        {
            _ = MessageParser.Default.Parse(raw, InternalDate, ct);
        }
        catch (MimeStructureLimitException ex)
        {
            rejected = ex;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var refusal = Require.Ref(rejected, "the structural-limit refusal");
        Assert.True(
            allocated < AllocationCeiling,
            $"Refusing a {BombParts}-part message allocated {allocated} bytes, so the tree was built first.");

        Assert.Contains("4096", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("multipart", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAsyncPathRefusesTheSameMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var raw = Bomb(BombParts);

        await Assert.ThrowsAsync<MimeStructureLimitException>(
            () => MessageParser.Default.ParseAsync(raw, InternalDate, ct));
    }

    [Fact]
    public void AForwardOnlySourceIsCutShortRatherThanFullyMaterialized()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = new MessageParser(new MessageParserOptions { MaxRawBoundaryLines = 16 });

        using var raw = Bomb(BombParts);
        using var forwardOnly = new ForwardOnlyStream(raw);

        MimeStructureLimitException? rejected = null;
        var before = GC.GetAllocatedBytesForCurrentThread();

        try
        {
            _ = parser.Parse(forwardOnly, InternalDate, ct);
        }
        catch (MimeStructureLimitException ex)
        {
            rejected = ex;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _ = Require.Ref(rejected, "the structural-limit refusal");
        Assert.True(
            allocated < AllocationCeiling,
            $"Refusing a forward-only {BombParts}-part message allocated {allocated} bytes.");

        Assert.True(
            forwardOnly.BytesRead < 4L * 1024 * 1024,
            $"The parser consumed {forwardOnly.BytesRead} bytes of a {raw.Length}-byte bomb before the cut.");
    }

    [Fact]
    public void ARefusalIsNeverAParsedMessageWithAnEmptyBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var raw = Bomb(BombParts);

        var rejected = Assert.ThrowsAny<FormatException>(() =>
        {
            _ = MessageParser.Default.Parse(raw, InternalDate, ct);
        });

        Assert.IsType<MimeStructureLimitException>(rejected);
    }

    [Fact]
    public void AnOrdinaryMultipartWithManyPartsStillParses()
    {
        var ct = TestContext.Current.CancellationToken;
        using var raw = Bomb(200);

        var parsed = MessageParser.Default.Parse(raw, InternalDate, ct);

        Assert.Contains("x", parsed.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            parsed.ParseWarnings,
            w => w.StartsWith("unparsable-message", StringComparison.Ordinal));
    }

    [Fact]
    public void ABodyFullOfDashedLinesIsNotMistakenForAPartCountBomb()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new StringBuilder();
        for (var i = 0; i < 6000; i++) body.Append("--- a/src/file").Append(i).Append(".c\r\n");

        var raw = Encoding.ASCII.GetBytes(
            "From: patches@example.test\r\n"
            + "To: bob@example.org\r\n"
            + "Subject: A very large patch\r\n"
            + "Date: Tue, 20 Jan 2026 08:00:00 +0000\r\n"
            + "Message-ID: <patch@example.test>\r\n"
            + "Content-Type: text/plain; charset=us-ascii\r\n"
            + "\r\n"
            + body.ToString());

        var parsed = MessageParser.Default.Parse(raw, InternalDate, ct);

        Assert.Contains("--- a/src/file0.c", parsed.BodyText, StringComparison.Ordinal);
        Assert.Equal("A very large patch", parsed.Subject);
    }

    private static MemoryStream Bomb(int parts)
    {
        var header = Encoding.ASCII.GetBytes(
            "From: bomb@example.test\r\n"
            + "To: bob@example.org\r\n"
            + "Subject: Many parts\r\n"
            + "Date: Tue, 20 Jan 2026 08:00:00 +0000\r\n"
            + "Message-ID: <bomb@example.test>\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: multipart/mixed; boundary=\"b\"\r\n"
            + "\r\n");

        var part = Encoding.ASCII.GetBytes("--b\r\nContent-Type: text/plain\r\n\r\nx\r\n");
        var trailer = Encoding.ASCII.GetBytes("--b--\r\n");

        var stream = new MemoryStream((header.Length + trailer.Length) + (part.Length * parts));
        stream.Write(header);
        for (var i = 0; i < parts; i++) stream.Write(part);
        stream.Write(trailer);
        stream.Position = 0;
        return stream;
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0) BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
