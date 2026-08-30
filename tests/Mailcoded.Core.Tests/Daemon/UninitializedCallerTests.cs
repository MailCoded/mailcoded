using System.Text;
using System.Text.Json;
using Mailcoded.Core.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

/// <summary>AGENT-INTERFACE §13.6: skipping the handshake must never buy capability.</summary>
public sealed class UninitializedCallerTests : IClassFixture<DaemonTranscriptFixture>
{
    private readonly DaemonTranscriptFixture _fixture;

    public UninitializedCallerTests(DaemonTranscriptFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_caller_that_never_initialized_is_refused_an_html_body()
    {
        using var document = JsonDocument.Parse(await ExchangeAsync(
            """{"jsonrpc":"2.0","id":1,"method":"message.get","params":{"messageId":1,"format":"html","fetchIfMissing":false}}"""));

        Assert.False(
            document.RootElement.TryGetProperty("result", out _),
            "A client that skipped initialize defaulted to the 'rpc' caller kind, the least restricted one, so "
            + "forgetting the handshake handed out HTML bodies and move access that a declared agent never gets.");

        Assert.Equal(
            (int)RpcErrorCode.Forbidden,
            document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Shutdown_stays_reachable_without_a_handshake()
    {
        using var document = JsonDocument.Parse(await ExchangeAsync(
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""));

        Assert.True(
            document.RootElement.TryGetProperty("result", out _),
            "initialize and shutdown are the two methods that must answer before any posture is negotiated, or a "
            + "client has no way to negotiate one and no way to stop the daemon.");
    }

    private async Task<string> ExchangeAsync(string request) =>
        await _fixture.Transcript.ExchangeAsync(
            Encoding.UTF8.GetBytes(request),
            TestContext.Current.CancellationToken);
}
