using ModelContextProtocol.Server;

namespace Mailcoded.Mcp;

/// <summary>The advertised tool list; mail-removal verbs are absent by construction, not gated.</summary>
internal static class ToolCatalog
{
    public static McpServerPrimitiveCollection<McpServerTool> Build(MailTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var collection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);

        collection.Add(MailcodedTool.Create(
            ToolText.SearchTool,
            "Search mail",
            ToolText.SearchDescription,
            ToolText.SearchSchema,
            tools.SearchAsync,
            readOnly: true,
            idempotent: true));

        collection.Add(MailcodedTool.Create(
            ToolText.ReadTool,
            "Read a message (plaintext)",
            ToolText.ReadDescription,
            ToolText.ReadSchema,
            tools.ReadAsync,
            readOnly: true,
            idempotent: true,
            openWorld: true));

        collection.Add(MailcodedTool.Create(
            ToolText.ThreadTool,
            "Read a conversation",
            ToolText.ThreadDescription,
            ToolText.ThreadSchema,
            tools.ThreadAsync,
            readOnly: true,
            idempotent: true));

        collection.Add(MailcodedTool.Create(
            ToolText.TagTool,
            "Tag a message",
            ToolText.TagDescription,
            ToolText.TagSchema,
            tools.TagAsync,
            readOnly: false,
            idempotent: true,
            openWorld: true));

        collection.Add(MailcodedTool.Create(
            ToolText.DraftTool,
            "Compose a draft",
            ToolText.DraftDescription,
            ToolText.DraftSchema,
            tools.DraftAsync,
            readOnly: true,
            idempotent: true));

        collection.Add(MailcodedTool.Create(
            ToolText.SendPreviewTool,
            "Preview a send (no mail leaves)",
            ToolText.SendPreviewDescription,
            ToolText.SendPreviewSchema,
            tools.SendPreviewAsync,
            readOnly: false));

        collection.Add(MailcodedTool.Create(
            ToolText.SendDraftTool,
            "Send a draft (human-confirmed)",
            ToolText.SendDraftDescription,
            ToolText.SendDraftSchema,
            tools.SendDraftAsync,
            readOnly: false,
            destructive: true,
            openWorld: true));

        collection.Add(MailcodedTool.Create(
            ToolText.StatsTool,
            "Store and policy counters",
            ToolText.StatsDescription,
            ToolText.StatsSchema,
            tools.StatsAsync,
            readOnly: true,
            idempotent: true));

        return collection;
    }
}
