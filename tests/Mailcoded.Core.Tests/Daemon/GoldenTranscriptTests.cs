using System.Text.Json;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class DaemonTranscriptFixture : IAsyncLifetime
{
    private DaemonTranscript? _transcript;

    internal DaemonTranscript Transcript =>
        _transcript ?? throw new InvalidOperationException("The transcript fixture was not initialized.");

    public async ValueTask InitializeAsync() =>
        _transcript = await DaemonTranscript.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_transcript is { } transcript) await transcript.DisposeAsync();
        _transcript = null;
    }
}

public sealed class GoldenTranscriptTests : IClassFixture<DaemonTranscriptFixture>
{
    private readonly DaemonTranscriptFixture _fixture;

    public GoldenTranscriptTests(DaemonTranscriptFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("initialize")]
    [InlineData("account-list")]
    [InlineData("folder-list")]
    [InlineData("folder-list-unknown-account")]
    [InlineData("sync-progress-unknown-account")]
    [InlineData("search")]
    [InlineData("search-is-automated")]
    [InlineData("search-not-automated")]
    [InlineData("search-is-verified")]
    [InlineData("search-not-verified")]
    [InlineData("search-in-anywhere")]
    [InlineData("search-in-junk")]
    [InlineData("search-semantic-unsupported")]
    [InlineData("message-get")]
    [InlineData("message-get-unsubscribe")]
    [InlineData("tags-set-unknown-message")]
    [InlineData("send-without-token")]
    [InlineData("send-preview-sender-not-allowed")]
    [InlineData("send-preview-unknown-signature")]
    [InlineData("store-backup-uninitialized")]
    [InlineData("store-backup-verify-uninitialized")]
    [InlineData("unknown-method")]
    [InlineData("malformed-json")]
    [InlineData("invalid-request")]
    [InlineData("provider-detect")]
    [InlineData("provider-detect-guess")]
    [InlineData("provider-detect-invalid-email")]
    public async Task Transcript_MatchesItsGoldenFile(string name)
    {
        var actual = await CanonicalResponseAsync(name);

        if (GoldenFiles.UpdateRequested())
        {
            GoldenFiles.WriteResponse(name + ".response.json", actual);
            return;
        }

        var expected = GoldenFiles.ReadResponse(name + ".response.json").TrimEnd('\n');

        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            GoldenJson.Describe(name, expected, actual));
    }

    [Fact]
    public async Task Initialize_AdvertisesTheDocumentedCapabilityShape()
    {
        using var document = JsonDocument.Parse(await ResponseAsync("initialize"));
        var capabilities = document.RootElement.GetProperty("result").GetProperty("capabilities");

        foreach (var required in new[]
                 {
                     "methods", "notifications", "providers", "search", "threading", "attachments",
                     "watch", "send", "rawSql", "htmlBodies", "semantic", "maxSearchLimit", "secretBackend",
                 })
        {
            Assert.True(capabilities.TryGetProperty(required, out _), $"capabilities.{required} is missing");
        }

        var methods = new List<string>();
        foreach (var method in capabilities.GetProperty("methods").EnumerateArray())
            methods.Add(method.GetString() ?? string.Empty);

        Assert.Equal(RpcMethods.Initialize, methods[0]);
        Assert.Contains(RpcMethods.Send, methods);
        Assert.DoesNotContain(methods, static m =>
            m.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || m.Contains("expunge", StringComparison.OrdinalIgnoreCase)
            || m.Contains("trash", StringComparison.OrdinalIgnoreCase)
            || m.Contains("purge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Initialize_AdvertisesEveryNotificationTheProtocolNames()
    {
        using var document = JsonDocument.Parse(await ResponseAsync("initialize"));
        var advertised = document.RootElement.GetProperty("result").GetProperty("capabilities")
            .GetProperty("notifications").EnumerateArray().Select(n => n.GetString()).ToList();

        var named = typeof(RpcNotifications)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (string?)f.GetRawConstantValue())
            .ToList();

        Assert.Equal(named.Order(StringComparer.Ordinal), advertised.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("folder-list-unknown-account", (int)RpcErrorCode.NotFound)]
    [InlineData("sync-progress-unknown-account", (int)RpcErrorCode.NotFound)]
    [InlineData("tags-set-unknown-message", (int)RpcErrorCode.NotFound)]
    [InlineData("send-without-token", (int)RpcErrorCode.ConfirmRequired)]
    [InlineData("send-preview-sender-not-allowed", (int)RpcErrorCode.InvalidParams)]
    [InlineData("send-preview-unknown-signature", (int)RpcErrorCode.InvalidParams)]
    [InlineData("store-backup-uninitialized", (int)RpcErrorCode.Forbidden)]
    [InlineData("store-backup-verify-uninitialized", (int)RpcErrorCode.Forbidden)]
    [InlineData("unknown-method", (int)RpcErrorCode.MethodNotFound)]
    [InlineData("malformed-json", (int)RpcErrorCode.ParseError)]
    [InlineData("invalid-request", (int)RpcErrorCode.InvalidRequest)]
    [InlineData("provider-detect-invalid-email", (int)RpcErrorCode.InvalidParams)]
    public async Task ErrorTranscripts_CarryTheDocumentedNumericCode(string name, int code)
    {
        using var document = JsonDocument.Parse(await ResponseAsync(name));
        var error = document.RootElement.GetProperty("error");

        Assert.Equal(code, error.GetProperty("code").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("result", out _), "an error response also carried a result");
    }

    private async Task<string> ResponseAsync(string name) =>
        await _fixture.Transcript.ExchangeAsync(
            GoldenFiles.ReadRequest(name + ".request.json"),
            TestContext.Current.CancellationToken);

    private async Task<string> CanonicalResponseAsync(string name) =>
        GoldenJson
            .Canonicalize(await ResponseAsync(name), _fixture.Transcript.StoreDirectory)
            .TrimEnd('\n');
}
