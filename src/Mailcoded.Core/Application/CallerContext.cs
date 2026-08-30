namespace Mailcoded.Core.Application;

/// <summary>Who is asking. The AGENT-INTERFACE §13.6 posture matrix is a function of this value alone.</summary>
public enum CallerKind
{
    /// <summary>An interactive client over JSON-RPC (the VS Code extension).</summary>
    Rpc,

    /// <summary>A shell-capable agent through the CLI.</summary>
    Cli,

    /// <summary>An agent host through the MCP adapter.</summary>
    Mcp,

    /// <summary>The daemon on its own behalf: retry loops, reconciliation, watch-driven sync.</summary>
    Internal,
}

/// <summary>The caller plus the best-effort host identity that every audit row records.</summary>
public readonly record struct CallerContext
{
    public CallerContext(CallerKind kind, string? agentHost = null)
    {
        Kind = kind;
        AgentHost = AuditText.Sanitize(agentHost, MaxAgentHostLength);
    }

    public const int MaxAgentHostLength = 64;
    public const string AgentHostEnvVar = "MAILCODED_AGENT_HOST";

    public CallerKind Kind { get; }

    /// <summary>Best-effort agent host name for the audit trail. Never a credential.</summary>
    public string? AgentHost { get; }

    public static readonly CallerContext Rpc = new(CallerKind.Rpc);
    public static readonly CallerContext Internal = new(CallerKind.Internal);

    public static CallerContext For(CallerKind kind, string? agentHost = null) => new(kind, agentHost);

    /// <summary>CLI and MCP: the surfaces the agent gates apply to.</summary>
    public bool IsAgentSurface => Kind is CallerKind.Cli or CallerKind.Mcp;

    /// <summary>The <c>sync_log.interface</c> value: cli | mcp | rpc | internal.</summary>
    public string InterfaceName => Kind switch
    {
        CallerKind.Cli => "cli",
        CallerKind.Mcp => "mcp",
        CallerKind.Rpc => "rpc",
        _ => "internal",
    };

    /// <summary>Best-effort host identification from the environment. Never throws.</summary>
    public static string? DetectAgentHost()
    {
        foreach (var name in HostEnvVars)
        {
            string? value;
            try
            {
                value = Environment.GetEnvironmentVariable(name);
            }
            catch (Exception)
            {
                continue;
            }

            var clean = AuditText.Sanitize(value, MaxAgentHostLength);
            if (!string.IsNullOrEmpty(clean)) return clean;
        }

        return null;
    }

    private static readonly string[] HostEnvVars =
    [
        AgentHostEnvVar,
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "TERM_PROGRAM",
    ];
}
