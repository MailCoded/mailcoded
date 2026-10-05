using System.Text;
using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Daemon;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class ServerDraftWireTests
{
    private static async Task<JsonDocument> CallAsync(DaemonTranscript transcript, string method, string parameters, CancellationToken ct)
    {
        var request = $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}""";
        return JsonDocument.Parse(await transcript.ExchangeAsync(Encoding.UTF8.GetBytes(request), "rpc", ct));
    }

    private const string Draft =
        "{\"accountId\":1,\"draft\":{\"to\":[\"bob@example.org\"],\"subject\":\"Hi\",\"bodyText\":\"Hello.\"}}";

    [Fact]
    public async Task Send_preview_says_off_when_the_account_does_not_save_drafts_to_the_server()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, RpcMethods.SendPreview, Draft, ct);
        var result = document.RootElement.GetProperty("result");

        Assert.Equal("off", result.GetProperty("serverDraft").GetString());
        Assert.False(result.TryGetProperty("serverDraftReason", out var reason) && reason.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Account_list_carries_the_setting_and_it_defaults_to_false()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);

        using var document = await CallAsync(transcript, RpcMethods.AccountList, "{}", ct);
        var account = document.RootElement.GetProperty("result").GetProperty("accounts")[0];

        Assert.False(account.GetProperty("saveDraftsToServer").GetBoolean());
    }

    [Fact]
    public async Task Outbox_list_names_the_server_draft_only_for_a_row_that_has_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transcript = await DaemonTranscript.StartAsync(ct);
        (await CallAsync(transcript, RpcMethods.SendPreview, Draft, ct)).Dispose();

        using var document = await CallAsync(transcript, RpcMethods.OutboxList, "{}", ct);
        var entry = Assert.Single(document.RootElement.GetProperty("result").GetProperty("entries").EnumerateArray());

        Assert.False(entry.TryGetProperty("serverDraft", out var state) && state.ValueKind != JsonValueKind.Null);
        Assert.False(entry.GetProperty("confirmed").GetBoolean());
    }

    [Fact]
    public void An_outbox_row_with_a_left_server_draft_maps_its_state_and_reason()
    {
        var row = new Mailcoded.Core.Store.OutboxRecord
        {
            Id = 7,
            AccountId = new Mailcoded.Core.Domain.Primitives.AccountId(1),
            MessageId = Mailcoded.Core.Domain.Primitives.MessageId.Parse("wire-1@example.com"),
            State = OutboxState.Sent,
            ServerDraft = new ServerDraft
            {
                FolderPath = "Drafts",
                UidValidity = new Mailcoded.Core.Domain.Primitives.UidValidity(5),
                Uid = new Mailcoded.Core.Domain.Primitives.Uid(9),
                State = ServerDraftState.Left,
                Reason = ServerDraftReason.NoMoveCapability,
            },
        };

        var dto = WireMapper.ToDto(row);

        Assert.Equal("left", dto.ServerDraft);
        Assert.Equal("no-move-capability", dto.ServerDraftReason);
        Assert.DoesNotContain("Drafts", System.Text.Json.JsonSerializer.Serialize(dto, ProtocolJsonContext.Default.OutboxEntryDto), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_value_the_wire_documents_is_one_the_domain_can_produce()
    {
        var states = Enum.GetValues<ServerDraftState>().Select(static s => s.ToWireValue()).ToHashSet();
        var reasons = Enum.GetValues<ServerDraftReason>().Select(static r => r.ToWireValue()).OfType<string>().ToHashSet();

        Assert.Equal(["saved", "moving", "moved", "left"], states.OrderBy(static s => Array.IndexOf(["saved", "moving", "moved", "left"], s)));
        Assert.Equal(13, reasons.Count);
        Assert.Equal(["off", "saved", "failed"], Enum.GetValues<ServerDraftSave>().Select(static s => s.ToWireValue()));
    }
}
