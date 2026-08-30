using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Integration.Support;

/// <summary>One daemon's worth of state over a store path; re-opening the same path with the same
/// secret store is how a crash-then-restart is simulated.</summary>
public sealed class MailHarness : IAsyncDisposable
{
    private MailHarness(SqliteStore store, ISecretStore secrets, MailStackFixture stack)
    {
        Store = store;
        Secrets = secrets;
        Stack = stack;

        Clock = SystemClock.Instance;
        Parser = MessageParser.Default;
        Audit = new AuditLog(store, Clock);
        Policy = new AgentPolicy(AgentPolicyOptions.Locked, Clock);
        Tokens = new ConfirmTokenStore(Clock);
        Sync = new SyncEngine(store, ReferencesThreader.Instance, Clock, Audit);
        Messages = new MessageService(store, Parser, Audit, Policy);
        Search = new SearchService(store, Policy, Audit);
        Send = new SendService(store, Parser, Clock, Audit, Policy, Tokens);
        Accounts = new AccountService(store, secrets, Sync, Audit);
    }

    public SqliteStore Store { get; }
    public ISecretStore Secrets { get; }
    public MailStackFixture Stack { get; }
    public IClock Clock { get; }
    public MessageParser Parser { get; }
    public AuditLog Audit { get; }
    public AgentPolicy Policy { get; }
    public ConfirmTokenStore Tokens { get; }
    public SyncEngine Sync { get; }
    public MessageService Messages { get; }
    public SearchService Search { get; }
    public SendService Send { get; }
    public AccountService Accounts { get; }

    public static MailHarness Open(TestWorkspace workspace, ISecretStore secrets, MailStackFixture stack)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(stack);

        var store = new SqliteStore(
            new SqliteStoreOptions { DatabasePath = workspace.DatabasePath, DataDirectory = workspace.Root },
            SystemClock.Instance);

        return new MailHarness(store, secrets, stack);
    }

    public string SecretRef { get; private set; } = "imap:integration-local";

    public async Task<AccountId> AddAccountAsync(CancellationToken ct)
    {
        var template = Stack.BuildAccountConfig(SecretRef);

        var accountId = await Accounts.AddAsync(
            new AddAccountRequest
            {
                Email = template.Email,
                DisplayName = template.DisplayName,
                Provider = template.Provider,
                Imap = template.Imap,
                Smtp = template.Smtp,
                Auth = template.Auth,
                SecretRef = SecretRef,
                Secret = Stack.Credentials.ImapPassword,
            },
            CallerContext.Rpc,
            ct).ConfigureAwait(false);

        return accountId;
    }

    public async Task<ImapProvider> ConnectProviderAsync(AccountId accountId, CancellationToken ct)
    {
        var account = Store.GetAccount(accountId, ct)
            ?? throw new InvalidOperationException($"No account with id {accountId.Value}.");

        var provider = new ImapProvider(Clock, TestTransportOptions.Fast);
        try
        {
            await provider.ConnectAsync(account, Secrets, ct).ConfigureAwait(false);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return provider;
    }

    /// <summary>smtp4dev authenticates with its own user, so the SMTP credential gets its own ref.</summary>
    public async Task<SmtpSender> ConnectSenderAsync(AccountId accountId, CancellationToken ct)
    {
        var account = Store.GetAccount(accountId, ct)
            ?? throw new InvalidOperationException($"No account with id {accountId.Value}.");

        var smtpRef = SecretRef + ":smtp";
        await Secrets.SetAsync(smtpRef, Stack.Credentials.SmtpPassword, ct).ConfigureAwait(false);

        var sender = new SmtpSender(Clock, TestTransportOptions.Fast);
        try
        {
            await sender.ConnectAsync(account with { SecretRef = smtpRef }, Secrets, ct).ConfigureAwait(false);
        }
        catch
        {
            await sender.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return sender;
    }

    public FolderSummary RequireFolder(AccountId accountId, string path, CancellationToken ct)
    {
        var wanted = FolderPath.Create(path);
        foreach (var folder in Store.ListFolders(accountId, ct))
        {
            if (folder.Path.NameEquals(wanted)) return folder;
        }

        throw new InvalidOperationException($"The store has no folder '{path}' for account {accountId.Value}.");
    }

    public ValueTask DisposeAsync()
    {
        Store.Dispose();
        return ValueTask.CompletedTask;
    }
}
