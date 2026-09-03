using System.Text;
using Mailcoded.Protocol;
using Mailcoded.Protocol.Client;
using Xunit;

namespace Mailcoded.Core.Tests.Protocol;

public sealed class FrameCodecTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"a\":\"ééé\"}")]
    [InlineData("{\"cjk\":\"项目进度\"}")]
    public async Task A_frame_round_trips_through_the_codec(string json)
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Encoding.UTF8.GetBytes(json);

        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, body, ct);
        stream.Position = 0;

        var frame = await FrameCodec.ReadAsync(stream, ct);

        Assert.Equal(FrameStatus.Message, frame.Status);
        Assert.Equal(json, Encoding.UTF8.GetString(frame.Body));
    }

    [Fact]
    public async Task The_header_counts_bytes_not_characters()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Encoding.UTF8.GetBytes("{\"s\":\"项目\"}");

        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, body, ct);

        var header = Encoding.ASCII.GetString(stream.ToArray()).Split("\r\n")[0];
        Assert.Equal($"Content-Length: {body.Length}", header);
    }

    [Fact]
    public async Task Back_to_back_frames_are_read_independently()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, "{\"n\":1}"u8.ToArray(), ct);
        await FrameCodec.WriteAsync(stream, "{\"n\":2}"u8.ToArray(), ct);
        stream.Position = 0;

        Assert.Equal("{\"n\":1}", Encoding.UTF8.GetString((await FrameCodec.ReadAsync(stream, ct)).Body));
        Assert.Equal("{\"n\":2}", Encoding.UTF8.GetString((await FrameCodec.ReadAsync(stream, ct)).Body));
    }

    [Fact]
    public async Task A_lower_case_header_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("content-length: 2\r\n\r\n{}"));

        var frame = await FrameCodec.ReadAsync(stream, ct);

        Assert.Equal(FrameStatus.Message, frame.Status);
    }

    [Fact]
    public async Task An_oversized_length_is_refused_before_allocating()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 999999999\r\n\r\n"));

        var frame = await FrameCodec.ReadAsync(stream, ct);

        Assert.Equal(FrameStatus.Malformed, frame.Status);
    }

    [Fact]
    public async Task A_closed_stream_reports_end_not_corruption()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();

        Assert.Equal(FrameStatus.EndOfStream, (await FrameCodec.ReadAsync(stream, ct)).Status);
    }
}
