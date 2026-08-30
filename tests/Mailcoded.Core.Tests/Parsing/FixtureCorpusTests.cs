using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class FixtureCorpusTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var name in Fixtures.EmlFileNames()) data.Add(name);
        return data;
    }

    [Fact]
    public void TheCorpusIsDiscoveredByWalkingUpFromTheTestBinary()
    {
        Assert.True(Directory.Exists(Fixtures.EmlDirectory));
        Assert.True(
            Fixtures.EmlFileNames().Count >= 35,
            $"Only {Fixtures.EmlFileNames().Count} fixtures found under '{Fixtures.EmlDirectory}'.");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void EveryFixtureParses(string fileName)
    {
        var ct = TestContext.Current.CancellationToken;

        using var stream = Fixtures.Open(fileName);
        var parsed = MessageParser.Default.Parse(stream, InternalDate, ct);

        Assert.NotNull(parsed.BodyText);
        Assert.NotNull(parsed.ParseWarnings);
        Assert.DoesNotContain(parsed.ParseWarnings, w => w.StartsWith("unparsable-message", StringComparison.Ordinal));
        Assert.True(parsed.DateUtc >= DateTimeOffset.UnixEpoch, $"{fileName} produced a pre-epoch date.");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void EveryFixtureYieldsFilesystemSafeAttachmentNames(string fileName)
    {
        var ct = TestContext.Current.CancellationToken;

        using var stream = Fixtures.Open(fileName);
        var parsed = MessageParser.Default.Parse(stream, InternalDate, ct);

        foreach (var attachment in parsed.Attachments)
        {
            Assert.False(string.IsNullOrEmpty(attachment.MimeType));

            var safe = AttachmentNaming.ToSaveAsFileName(attachment.FileName, attachment.Index, attachment.MimeType);
            Assert.False(string.IsNullOrEmpty(safe));
            Assert.Equal(safe, Path.GetFileName(safe));
            Assert.DoesNotContain("/", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", safe, StringComparison.Ordinal);
            Assert.DoesNotContain(":", safe, StringComparison.Ordinal);
            Assert.NotEqual(".", safe);
            Assert.NotEqual("..", safe);
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void ParsingFromBytesAndFromAStreamAgree(string fileName)
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = Fixtures.Read(fileName);

        var fromBytes = MessageParser.Default.Parse(bytes, InternalDate, ct);

        using var stream = Fixtures.Open(fileName);
        var hashed = MessageParser.Default.ParseAndHash(stream, InternalDate, ct);

        Assert.Equal(fromBytes.Subject, hashed.Message.Subject);
        Assert.Equal(fromBytes.BodyText, hashed.Message.BodyText);
        Assert.Equal(fromBytes.Attachments.Count, hashed.Message.Attachments.Count);
        Assert.Equal(bytes.LongLength, hashed.RawSize);
        Assert.Equal(RawMessageHasher.ComputeSha256Hex(bytes), hashed.Sha256Hex);
        Assert.Equal(64, hashed.Sha256Hex.Length);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void ParsingIsCancellable(string fileName)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        using var stream = Fixtures.Open(fileName);
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = MessageParser.Default.Parse(stream, InternalDate, cancelled.Token);
        });
    }
}
