using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

/// <summary>Over the wire there is no way to remove an account, so a credential the server refuses
/// must not leave one behind. That is the whole reason `verify` exists.</summary>
public sealed class AccountAddVerifyTests : IClassFixture<DaemonTranscriptFixture>
{
    private readonly DaemonTranscriptFixture _fixture;

    public AccountAddVerifyTests(DaemonTranscriptFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_verified_add_the_server_refuses_stores_no_account()
    {
        var ct = TestContext.Current.CancellationToken;

        // Accepts then hangs up without a greeting: a fast, deterministic failure rather than a
        // 30-second connect timeout against a dead port.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        var hangup = Task.Run(
            async () =>
            {
                try
                {
                    using var accepted = await probe.AcceptTcpClientAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                }
            },
            CancellationToken.None);

        try
        {
            var before = await AccountCountAsync(ct);

            await ExchangeAsync(
                """{"jsonrpc":"2.0","id":80,"method":"secret.set","params":{"ref":"probe:verify","value":"nonsense"}}""",
                ct);

            var response = await ExchangeAsync(
                $$$"""
                {"jsonrpc":"2.0","id":81,"method":"account.add","params":{"email":"verify@example.test","imap":{"host":"127.0.0.1","port":{{{port}}},"security":"none"},"secretRef":"probe:verify","verify":true}}
                """,
                ct);

            using var document = JsonDocument.Parse(response);

            Assert.False(document.RootElement.TryGetProperty("result", out _), "a refused login still created an account");
            Assert.Equal((int)RpcErrorCode.Network, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(before, await AccountCountAsync(ct));
        }
        finally
        {
            probe.Stop();
            await hangup;
        }
    }

    [Fact]
    public async Task An_unverified_add_stores_without_touching_the_network()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await AccountCountAsync(ct);

        await ExchangeAsync(
            """{"jsonrpc":"2.0","id":82,"method":"secret.set","params":{"ref":"probe:plain","value":"nonsense"}}""",
            ct);

        var response = await ExchangeAsync(
            """
            {"jsonrpc":"2.0","id":83,"method":"account.add","params":{"email":"plain@example.test","imap":{"host":"127.0.0.1","port":1,"security":"none"},"secretRef":"probe:plain"}}
            """,
            ct);

        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");

        Assert.True(result.GetProperty("accountId").GetInt64() > 0);
        Assert.False(result.TryGetProperty("verified", out var verified) && verified.GetBoolean());
        Assert.Equal(before + 1, await AccountCountAsync(ct));
    }

    private async Task<int> AccountCountAsync(CancellationToken ct)
    {
        var response = await ExchangeAsync("""{"jsonrpc":"2.0","id":84,"method":"account.list"}""", ct);
        using var document = JsonDocument.Parse(response);

        return document.RootElement.GetProperty("result").GetProperty("accounts").GetArrayLength();
    }

    private Task<string> ExchangeAsync(string request, CancellationToken ct) =>
        _fixture.Transcript.ExchangeAsync(Encoding.UTF8.GetBytes(request.Trim()), ct);
}
