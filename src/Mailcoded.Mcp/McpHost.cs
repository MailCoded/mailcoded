using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Mcp;

/// <summary>The hand-wired composition root. Hosts hold no business logic (CLAUDE.md invariant 8).</summary>
internal sealed class McpHost : IAsyncDisposable
{
    private McpHost(
        SqliteStore store,
        MailConnections connections,
        SearchService search,
        MessageService messages,
        SendService send,
        HealthMonitor health,
        AgentPolicy policy,
        CallerContext caller)
    {
        Store = store;
        Connections = connections;
        Search = search;
        Messages = messages;
        Send = send;
        Health = health;
        Policy = policy;
        Caller = caller;
    }

    public SqliteStore Store { get; }
    public MailConnections Connections { get; }
    public SearchService Search { get; }
    public MessageService Messages { get; }
    public SendService Send { get; }
    public HealthMonitor Health { get; }
    public AgentPolicy Policy { get; }
    public CallerContext Caller { get; }

    public static McpHost Create(string? databasePath)
    {
        IClock clock = SystemClock.Instance;
        var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = databasePath }, clock);
        var secrets = SecretStoreFactory.Create(store.DataDirectory);
        var parser = new MessageParser();
        var audit = new AuditLog(store, clock);
        var policy = AgentPolicy.FromEnvironment(clock);
        var tokens = new ConfirmTokenStore(clock);
        var registry = new ConnectionRegistry(clock);

        return new McpHost(
            store,
            new MailConnections(store, secrets, registry, clock),
            new SearchService(store, policy, audit),
            new MessageService(store, parser, audit, policy),
            new SendService(store, parser, clock, audit, policy, tokens),
            new HealthMonitor(store, registry, policy, tokens, audit, clock, secrets.BackendName),
            policy,
            CallerContext.For(CallerKind.Mcp, CallerContext.DetectAgentHost()));
    }

    public async ValueTask DisposeAsync()
    {
        await Connections.DisposeAsync().ConfigureAwait(false);
        Store.Dispose();
    }
}
