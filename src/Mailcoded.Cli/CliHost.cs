using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Cli;

/// <summary>
/// The hand-wired composition root. It constructs adapters and nothing else — every safety gate
/// lives in Mailcoded.Core and is neither re-implemented nor bypassed here (CLAUDE invariant 8).
/// </summary>
internal sealed class CliHost : IAsyncDisposable
{
    public const string DataDirEnvVar = "MAILCODED_DATA_DIR";

    private readonly List<IAsyncDisposable> _connections = [];
    private bool _disposed;

    private CliHost(SqliteStore store, ISecretStore secrets)
    {
        Store = store;
        Secrets = secrets;
        Clock = store.Clock;
        Parser = MessageParser.Default;
        Threader = ReferencesThreader.Instance;
        Audit = new AuditLog(store, Clock);
        Policy = AgentPolicy.FromEnvironment(Clock);
        Tokens = new ConfirmTokenStore(Clock);
        Connections = new ConnectionRegistry(Clock);
        Sync = new SyncEngine(store, Threader, Clock, Audit, SyncOptions.Default);
        Search = new SearchService(store, Policy, Audit, SearchServiceOptions.Default);
        Messages = new MessageService(store, Parser, Audit, Policy, MessageServiceOptions.Default);
        Send = new SendService(store, Parser, Clock, Audit, Policy, Tokens, SendOptions.Default);
        Accounts = new AccountService(store, secrets, Sync, Audit);
        Health = new HealthMonitor(store, Connections, Policy, Tokens, Audit, Clock, secrets.BackendName);
        Caller = CallerContext.For(CallerKind.Cli, CallerContext.DetectAgentHost());
    }

    public static CliHost Create(CommandLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var dataDirectory = line.Value("data-dir") ?? Environment.GetEnvironmentVariable(DataDirEnvVar);
        if (string.IsNullOrWhiteSpace(dataDirectory)) dataDirectory = null;

        var store = new SqliteStore(
            new SqliteStoreOptions { DatabasePath = line.Value("db"), DataDirectory = dataDirectory },
            SystemClock.Instance);

        try
        {
            return new CliHost(store, SecretStoreFactory.Create(store.DataDirectory));
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public SqliteStore Store { get; }
    public ISecretStore Secrets { get; }
    public IClock Clock { get; }
    public MessageParser Parser { get; }
    public IThreader Threader { get; }
    public AuditLog Audit { get; }
    public AgentPolicy Policy { get; }
    public ConfirmTokenStore Tokens { get; }
    public ConnectionRegistry Connections { get; }
    public SyncEngine Sync { get; }
    public SearchService Search { get; }
    public MessageService Messages { get; }
    public SendService Send { get; }
    public AccountService Accounts { get; }
    public HealthMonitor Health { get; }
    public CallerContext Caller { get; }

    /// <summary>Resolves <c>--account</c>, or the only account when the store holds exactly one.</summary>
    public AccountConfig RequireAccount(CommandLine line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);

        var accounts = Store.ListAccounts(ct);
        var requested = line.Value("account");

        if (requested is not null)
        {
            if (long.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                foreach (var account in accounts)
                    if (account.Id.Value == id) return account;
            }

            foreach (var account in accounts)
                if (string.Equals(account.Email, requested, StringComparison.OrdinalIgnoreCase)) return account;

            throw new StoreException(FailureCategory.NotFound, $"No account matches '{requested}'.");
        }

        if (accounts.Count == 1) return accounts[0];

        if (accounts.Count == 0)
        {
            throw new StoreException(
                FailureCategory.NotFound,
                "No account is configured. Run 'mailcoded account add --help'.");
        }

        var ids = new List<string>(accounts.Count);
        foreach (var account in accounts)
            ids.Add(account.Id.Value.ToString(CultureInfo.InvariantCulture) + "=" + account.Email);

        throw new CliUsageException(
            "This store holds several accounts; pass --account <id|email>. Known: " + string.Join(", ", ids));
    }

    public async Task<IMailProvider> ConnectProviderAsync(AccountConfig account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        var provider = new ImapProvider(
            Clock,
            MailTransportOptions.Default,
            AccountTokenSources.For(account, Secrets));
        Connections.Observe(account.Id, ConnectionRole.Imap, ConnectionState.Connecting);

        try
        {
            await provider.ConnectAsync(account, Secrets, ct).ConfigureAwait(false);
        }
        catch
        {
            Connections.Observe(account.Id, ConnectionRole.Imap, ConnectionState.Error);
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        Connections.Observe(account.Id, ConnectionRole.Imap, ConnectionState.Connected);
        _connections.Add(provider);
        return provider;
    }

    public async Task<IMailSender> ConnectSenderAsync(AccountConfig account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.Smtp is null)
        {
            throw new StoreException(
                FailureCategory.NotFound,
                $"Account {account.Id.Value} has no SMTP configuration; add one before sending.");
        }

        var sender = new SmtpSender(
            Clock,
            MailTransportOptions.Default,
            AccountTokenSources.For(account, Secrets));
        Connections.Observe(account.Id, ConnectionRole.Smtp, ConnectionState.Connecting);

        try
        {
            await sender.ConnectAsync(account, Secrets, ct).ConfigureAwait(false);
        }
        catch
        {
            Connections.Observe(account.Id, ConnectionRole.Smtp, ConnectionState.Error);
            await sender.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        Connections.Observe(account.Id, ConnectionRole.Smtp, ConnectionState.Connected);
        _connections.Add(sender);
        return sender;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        for (var i = _connections.Count - 1; i >= 0; i--)
        {
            try
            {
                await _connections[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The process is exiting; a failed LOGOUT must not mask the command's own result.
            }
        }

        Store.Dispose();
    }
}
