using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class SenderIdentityMcpTests
{
    private const string Own = "golden@example.test";

    private static async Task<(TempWorkspace Workspace, McpHost Host, MailTools Tools)> StartAsync(CancellationToken ct)
    {
        var workspace = TempWorkspace.Create("mcp-sender-identity");
        var host = McpHost.Create(workspace.DatabasePath);
        var account = await StoreSeeder.AddAccountAsync(host.Store, ct);

        Assert.True(SenderIdentity.TryCreate("sales@example.test", "Sales Desk", "help@example.test", out var identity, out _));
        await host.Store.EditIdentitiesAsync(account, list => SenderIdentities.With(list, identity), ct);
        await host.Store.EditSignaturesAsync(
            account,
            book => book.Set("work", "Alex Example").Set("reply", "Alex")
                .WithDefault(SignatureUse.New, "work").WithDefault(SignatureUse.Reply, "reply"),
            ct);

        return (workspace, host, new MailTools(host));
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return new ToolArgs(values);
    }

    private static string DraftArgs(string extra = "") =>
        "{\"account_id\":1,\"to\":[\"bob@example.org\"],\"subject\":\"Hi\",\"body\":\"Hello.\"" + (extra.Length > 0 ? "," + extra : string.Empty) + "}";

    [Fact]
    public async Task Draft_refuses_a_from_the_account_does_not_own_with_a_stable_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        var failure = await Assert.ThrowsAsync<SenderNotAllowedException>(
            () => tools.DraftAsync(Args(DraftArgs("\"from\":\"ceo@example.org\"")), ct));

        var described = ToolErrors.Describe(failure);
        Assert.False(described.Ok);
        Assert.Equal("invalid_params", described.ErrorCode);
        Assert.Equal("sender_not_allowed", described.Reason);
        Assert.DoesNotContain("ceo@example.org", described.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_preview_refuses_it_too_and_queues_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        await Assert.ThrowsAsync<SenderNotAllowedException>(
            () => tools.SendPreviewAsync(Args(DraftArgs("\"from\":\"ceo@example.org\"")), ct));

        Assert.Empty(host.Send.ListOutbox(null, ct));
    }

    [Fact]
    public async Task An_identity_from_is_accepted_in_any_case_by_both_tools()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        using var draft = JsonDocument.Parse(await tools.DraftAsync(Args(DraftArgs("\"from\":\"SALES@example.test\"")), ct));
        Assert.Equal("sales@example.test", draft.RootElement.GetProperty("from").GetString());

        using var preview = JsonDocument.Parse(await tools.SendPreviewAsync(Args(DraftArgs("\"from\":\"Sales@Example.Test\"")), ct));
        Assert.Equal("sales@example.test", preview.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public async Task Without_a_from_the_account_address_is_used_and_shown()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        using var draft = JsonDocument.Parse(await tools.DraftAsync(Args(DraftArgs()), ct));

        Assert.Equal(Own, draft.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public async Task The_default_signature_is_applied_and_the_preview_the_human_sees_shows_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        using var draft = JsonDocument.Parse(await tools.DraftAsync(Args(DraftArgs()), ct));
        Assert.Equal("Hello.\n\n-- \nAlex Example\n", draft.RootElement.GetProperty("body_preview").GetString());
        Assert.Equal("work", draft.RootElement.GetProperty("signature").GetString());
        Assert.Equal("Hello.\n\n-- \nAlex Example\n".Length, draft.RootElement.GetProperty("body_chars").GetInt32());

        using var preview = JsonDocument.Parse(await tools.SendPreviewAsync(Args(DraftArgs()), ct));
        Assert.Equal("Hello.\n\n-- \nAlex Example\n", preview.RootElement.GetProperty("body_preview").GetString());
        Assert.Equal("work", preview.RootElement.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task A_reply_gets_the_reply_default_and_a_named_signature_or_none_overrides_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        using var reply = JsonDocument.Parse(await tools.DraftAsync(Args(DraftArgs("\"in_reply_to\":\"orig-1@example.net\"")), ct));
        Assert.Equal("reply", reply.RootElement.GetProperty("signature").GetString());

        using var named = JsonDocument.Parse(await tools.DraftAsync(Args(DraftArgs("\"signature\":\"REPLY\"")), ct));
        Assert.Equal("reply", named.RootElement.GetProperty("signature").GetString());

        using var none = JsonDocument.Parse(await tools.DraftAsync(Args(DraftArgs("\"no_signature\":true")), ct));
        Assert.False(none.RootElement.TryGetProperty("signature", out _));
        Assert.Equal("Hello.", none.RootElement.GetProperty("body_preview").GetString());

        using var both = JsonDocument.Parse(await tools.DraftAsync(Args(DraftArgs("\"signature\":\"work\",\"no_signature\":true")), ct));
        Assert.Equal("work", both.RootElement.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task An_unknown_signature_is_invalid_params()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => tools.DraftAsync(Args(DraftArgs("\"signature\":\"nope\"")), ct));

        Assert.IsNotType<SenderNotAllowedException>(failure);
        Assert.Equal("invalid_params", ToolErrors.Describe(failure).ErrorCode);
    }

    [Fact]
    public async Task Drafting_for_an_account_that_does_not_exist_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.DraftAsync(Args(DraftArgs().Replace("\"account_id\":1", "\"account_id\":99", StringComparison.Ordinal)), ct));

        Assert.Equal("not_found", ToolErrors.Describe(failure).ErrorCode);
    }

    [Fact]
    public async Task No_tool_can_add_an_identity_or_change_a_signature()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workspace, host, tools) = await StartAsync(ct);
        using var scopeWorkspace = workspace;
        await using var scopeHost = host;

        var catalog = ToolCatalog.Build(tools).Select(t => t.ProtocolTool).ToArray();

        Assert.DoesNotContain(catalog, t =>
            t.Name.Contains("identity", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("signature", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("alias", StringComparison.OrdinalIgnoreCase));

        var schemas = string.Join('\n', catalog.Select(t => t.InputSchema.GetRawText()));
        Assert.Contains("\"signature\"", schemas, StringComparison.Ordinal);
        Assert.Contains("\"no_signature\"", schemas, StringComparison.Ordinal);
        await Task.CompletedTask;
    }
}
