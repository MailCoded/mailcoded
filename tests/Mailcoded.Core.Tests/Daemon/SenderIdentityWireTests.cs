using System.Text;
using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Daemon;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class SenderIdentityWireTests
{
    private static async Task<JsonDocument> CallAsync(DaemonTranscript transcript, string method, string parameters, CancellationToken ct)
    {
        var request = $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}""";
        return JsonDocument.Parse(await transcript.ExchangeAsync(Encoding.UTF8.GetBytes(request), "rpc", ct));
    }

    private static string Draft(string extra) =>
        "{\"accountId\":1,\"draft\":{\"to\":[\"bob@example.org\"],\"subject\":\"Hi\",\"bodyText\":\"Hello.\"" + extra + "}}";

    [Fact]
    public async Task Account_list_carries_the_identities_the_signatures_and_both_defaults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, "account.list", "{}", ct);
        var account = document.RootElement.GetProperty("result").GetProperty("accounts")[0];

        var identity = Assert.Single(account.GetProperty("identities").EnumerateArray());
        Assert.Equal("sales@example.test", identity.GetProperty("address").GetString());
        Assert.Equal("Sales Desk", identity.GetProperty("displayName").GetString());
        Assert.Equal("help@example.test", identity.GetProperty("replyTo").GetString());

        var signature = Assert.Single(account.GetProperty("signatures").EnumerateArray());
        Assert.Equal("work", signature.GetProperty("name").GetString());
        Assert.Equal("Golden Tester\nExample Pty", signature.GetProperty("text").GetString());
        Assert.Equal("work", account.GetProperty("defaultSignatureNew").GetString());
        Assert.Equal("work", account.GetProperty("defaultSignatureReply").GetString());
    }

    [Fact]
    public async Task A_from_the_account_does_not_own_is_invalid_params_with_a_machine_readable_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, "send.preview", Draft(",\"from\":\"ceo@example.org\""), ct);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal((int)RpcErrorCode.InvalidParams, error.GetProperty("code").GetInt32());
        Assert.Equal("sender_not_allowed", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.DoesNotContain("ceo@example.org", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_identity_from_is_accepted_in_any_case_and_the_preview_names_the_configured_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, "send.preview", Draft(",\"from\":\"SALES@Example.test\""), ct);
        var preview = document.RootElement.GetProperty("result").GetProperty("preview");

        Assert.Equal("sales@example.test", preview.GetProperty("from").GetString());
        Assert.Equal("Hello.", preview.GetProperty("bodyPreview").GetString());
    }

    [Fact]
    public async Task Without_a_signature_option_the_body_is_exactly_what_the_client_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, "send.preview", Draft(""), ct);
        var preview = document.RootElement.GetProperty("result").GetProperty("preview");

        Assert.Equal("Hello.", preview.GetProperty("bodyPreview").GetString());
        Assert.False(preview.GetProperty("bodyTruncated").GetBoolean());
        Assert.Equal("golden@example.test", preview.GetProperty("from").GetString());
    }

    [Fact]
    public async Task A_named_signature_or_the_default_is_inserted_above_the_quoted_text()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);
        const string signed = "Hello.\n\n-- \nGolden Tester\nExample Pty\n";

        using var named = await CallAsync(transcript, "send.preview", Draft(",\"signature\":\"WORK\""), ct);
        Assert.Equal(signed, named.RootElement.GetProperty("result").GetProperty("preview").GetProperty("bodyPreview").GetString());

        using var useDefault = await CallAsync(transcript, "send.preview", Draft(",\"useDefaultSignature\":true"), ct);
        Assert.Equal(signed, useDefault.RootElement.GetProperty("result").GetProperty("preview").GetProperty("bodyPreview").GetString());

        using var quoted = await CallAsync(transcript, "send.preview", Draft(",\"signature\":\"work\",\"quotedText\":\"> Hi\\n\""), ct);
        Assert.Equal(signed + "\n> Hi\n", quoted.RootElement.GetProperty("result").GetProperty("preview").GetProperty("bodyPreview").GetString());
    }

    [Fact]
    public async Task The_error_mapper_gives_the_reason_and_leaves_other_argument_errors_without_one()
    {
        var log = new StderrLog(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);

        var denied = RpcErrorMapper.Map(new SenderNotAllowedException(), log);
        Assert.Equal((int)RpcErrorCode.InvalidParams, denied.Code);
        Assert.Equal("sender_not_allowed", denied.Data?.Reason);
        Assert.Equal("protocol", denied.Data?.Category);

        var other = RpcErrorMapper.Map(new ArgumentException("nope"), log);
        Assert.Equal((int)RpcErrorCode.InvalidParams, other.Code);
        Assert.Null(other.Data?.Reason);

        await Task.CompletedTask;
    }
}
