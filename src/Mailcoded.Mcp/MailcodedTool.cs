using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailcoded.Mcp;

internal delegate Task<string> ToolHandler(ToolArgs args, CancellationToken ct);

/// <summary>One explicitly registered tool; the SDK's reflection-based discovery stays unused.</summary>
internal sealed class MailcodedTool : McpServerTool
{
    private readonly Tool _tool;
    private readonly ToolHandler _handler;

    private MailcodedTool(Tool tool, ToolHandler handler)
    {
        _tool = tool;
        _handler = handler;
    }

    public override Tool ProtocolTool => _tool;

    public override IReadOnlyList<object> Metadata => [];

    public static MailcodedTool Create(
        string name,
        string title,
        string description,
        string inputSchema,
        ToolHandler handler,
        bool readOnly,
        bool destructive = false,
        bool idempotent = false,
        bool openWorld = false)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var tool = new Tool
        {
            Name = name,
            Title = title,
            Description = ToolText.Describe(description),
            InputSchema = ParseSchema(inputSchema),
            Annotations = new ToolAnnotations
            {
                Title = title,
                ReadOnlyHint = readOnly,
                DestructiveHint = destructive,
                IdempotentHint = idempotent,
                OpenWorldHint = openWorld,
            },
        };

        return new MailcodedTool(tool, handler);
    }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var json = await _handler(new ToolArgs(request.Params?.Arguments), cancellationToken).ConfigureAwait(false);
            return Result(json, isError: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = ToolErrors.Describe(ex);
            return Result(JsonSerializer.Serialize(error, McpJsonContext.Default.ToolErrorDto), isError: true);
        }
    }

    private static CallToolResult Result(string json, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = json }],
        IsError = isError,
    };

    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}
