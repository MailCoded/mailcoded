using System.Text;
using System.Text.Json;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class CorrespondentWireTests
{
    private static async Task<JsonDocument> CallAsync(DaemonTranscript transcript, string parameters, CancellationToken ct)
    {
        var request = $$"""{"jsonrpc":"2.0","id":1,"method":"correspondent.suggest","params":{{parameters}}}""";
        return JsonDocument.Parse(await transcript.ExchangeAsync(Encoding.UTF8.GetBytes(request), "rpc", ct));
    }

    private static string[] Addresses(JsonDocument document) =>
        document.RootElement.GetProperty("result").GetProperty("correspondents").EnumerateArray()
            .Select(c => c.GetProperty("address").GetString()!).ToArray();

    [Fact]
    public async Task The_method_is_advertised_in_the_capabilities()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientName":"t","clientVersion":"0","interface":"rpc"}}""";
        using var document = JsonDocument.Parse(await transcript.ExchangeAsync(Encoding.UTF8.GetBytes(request), ct));
        var methods = document.RootElement.GetProperty("result").GetProperty("capabilities").GetProperty("methods")
            .EnumerateArray().Select(m => m.GetString()).ToList();

        Assert.Contains(RpcMethods.CorrespondentSuggest, methods);
    }

    [Theory]
    [InlineData("{\"accountId\":1,\"limit\":101}")]
    [InlineData("{\"accountId\":1,\"limit\":0}")]
    [InlineData("{\"accountId\":1,\"limit\":-3}")]
    public async Task A_limit_outside_one_to_a_hundred_is_invalid_params(string parameters)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, parameters, ct);

        Assert.Equal((int)RpcErrorCode.InvalidParams, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accountId\":\"one\"}")]
    [InlineData("{\"accountId\":1,\"prefix\":5}")]
    [InlineData("{\"accountId\":1,\"limit\":\"ten\"}")]
    public async Task Missing_or_mistyped_params_are_invalid_params(string parameters)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, parameters, ct);

        Assert.Equal((int)RpcErrorCode.InvalidParams, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task The_largest_allowed_limit_and_a_null_prefix_work()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, "{\"accountId\":1,\"limit\":100,\"prefix\":null}", ct);

        Assert.Equal(["ada@example.test", "ada.lovelace@example.net", "bob@example.test"], Addresses(document));
    }

    [Theory]
    [InlineData("bob", new[] { "bob@example.test" })]
    [InlineData("Builder", new[] { "bob@example.test" })]
    [InlineData("lovelace", new string[0])]
    [InlineData("ada.l", new[] { "ada.lovelace@example.net" })]
    [InlineData("nobody", new string[0])]
    public async Task The_prefix_matches_addresses_and_name_words(string prefix, string[] expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, $"{{\"accountId\":1,\"prefix\":\"{prefix}\"}}", ct);

        Assert.Equal(expected, Addresses(document));
    }

    [Fact]
    public async Task A_huge_or_hostile_prefix_is_answered_not_crashed_on()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var huge = await CallAsync(transcript, "{\"accountId\":1,\"prefix\":\"" + new string('a', 200_000) + "\"}", ct);
        Assert.Empty(Addresses(huge));

        using var hostile = await CallAsync(transcript, "{\"accountId\":1,\"prefix\":\"'; DROP TABLE correspondents;--\\u0000\\r\\n\"}", ct);
        Assert.Empty(Addresses(hostile));

        using var again = await CallAsync(transcript, "{\"accountId\":1}", ct);
        Assert.Equal(3, Addresses(again).Length);
    }
}
