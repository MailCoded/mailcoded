using System.Text;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>The credential travels in <see cref="Secret"/> to the secret store and nowhere else.</summary>
public sealed record AddAccountRequest
{
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public ProviderKind Provider { get; init; } = ProviderKind.Imap;
    public required ImapConfig Imap { get; init; }
    public SmtpConfig? Smtp { get; init; }
    public AuthKind Auth { get; init; } = AuthKind.Password;

    /// <summary>Existing handle to reuse; one is derived from the address when this is null.</summary>
    public string? SecretRef { get; init; }

    /// <summary>Written straight to the secret store. Never persisted, logged, or echoed.</summary>
    public string? Secret { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append("Email = ").Append(Email);
        builder.Append(", Provider = ").Append(Provider);
        builder.Append(", Auth = ").Append(Auth);
        builder.Append(", SecretRef = ").Append(SecretRedactor.SafeRef(SecretRef));
        return true;
    }
}

public sealed record AccountSummary
{
    public required AccountId Id { get; init; }
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public ProviderKind Provider { get; init; }
    public AuthKind Auth { get; init; }
    public string ImapHost { get; init; } = string.Empty;
    public int ImapPort { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; }
    public ServerQuirks Quirks { get; init; } = ServerQuirks.None;
    public int FolderCount { get; init; }
    public int UnreadCount { get; init; }
}

/// <summary>Adds and lists accounts, and drives the first sync. Never touches a credential value.</summary>
public sealed class AccountService
{
    public const string SecretRefPrefix = "imap:";

    private readonly SqliteStore _store;
    private readonly ISecretStore _secrets;
    private readonly SyncEngine _sync;
    private readonly AuditLog _audit;

    public AccountService(SqliteStore store, ISecretStore secrets, SyncEngine sync, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(audit);

        _store = store;
        _secrets = secrets;
        _sync = sync;
        _audit = audit;
    }

    public string SecretBackendName => _secrets.BackendName;

    public async Task<AccountId> AddAsync(AddAccountRequest request, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!EmailAddress.TryParse(request.Email, out var email))
            throw new ArgumentException("The account address is not a valid email address.", nameof(request));

        var secretRef = string.IsNullOrWhiteSpace(request.SecretRef)
            ? DeriveSecretRef(email)
            : request.SecretRef.Trim();

        if (!string.IsNullOrEmpty(request.Secret))
            await _secrets.SetAsync(secretRef, request.Secret, ct).ConfigureAwait(false);

        var config = new AccountConfig
        {
            Email = email.Value,
            DisplayName = request.DisplayName,
            Provider = request.Provider,
            Imap = request.Imap,
            Smtp = request.Smtp,
            Auth = request.Auth,
            SecretRef = secretRef,
        };

        var accountId = await _store.AddAccountAsync(config, ct).ConfigureAwait(false);

        await _audit.InfoAsync(
            AuditEvents.AccountAdded,
            accountId,
            caller,
            AuditText.Fields(
                ("provider", request.Provider.ToWireValue()),
                ("auth", request.Auth.ToString()),
                ("host", request.Imap.Host),
                ("backend", _secrets.BackendName)),
            ct).ConfigureAwait(false);

        return accountId;
    }

    public async Task UpdateAsync(AccountConfig config, CallerContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _store.UpdateAccountAsync(config, ct).ConfigureAwait(false);
        await _audit.InfoAsync(
            AuditEvents.AccountUpdated,
            config.Id,
            caller,
            AuditText.Fields(("host", config.Imap.Host)),
            ct).ConfigureAwait(false);
    }

    /// <summary>Stores a credential under an opaque handle. The value never reaches any other sink.</summary>
    public Task SetSecretAsync(string secretRef, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        ArgumentException.ThrowIfNullOrEmpty(value);

        return _secrets.SetAsync(secretRef.Trim(), value, ct);
    }

    /// <summary>True when a credential is retrievable for the account, without revealing it.</summary>
    public async Task<bool> HasCredentialAsync(AccountId accountId, CancellationToken ct)
    {
        var account = _store.GetAccount(accountId, ct);
        if (account is null) return false;

        // An OAuth account holds no password; its credential is the token cache beside the ref.
        var reference = account.Auth == AuthKind.OAuth2
            ? OAuthOptions.CacheRefFor(account.SecretRef)
            : account.SecretRef;

        var value = await _secrets.GetAsync(reference, ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(value);
    }

    public IReadOnlyList<AccountSummary> List(CancellationToken ct = default)
    {
        var accounts = _store.ListAccounts(ct);
        var summaries = new List<AccountSummary>(accounts.Count);

        foreach (var account in accounts)
        {
            var folders = _store.ListFolders(account.Id, ct);
            var unread = 0;
            foreach (var folder in folders) unread += folder.UnreadCount;

            summaries.Add(new AccountSummary
            {
                Id = account.Id,
                Email = account.Email,
                DisplayName = account.DisplayName,
                Provider = account.Provider,
                Auth = account.Auth,
                ImapHost = account.Imap.Host,
                ImapPort = account.Imap.Port,
                SmtpHost = account.Smtp?.Host,
                SmtpPort = account.Smtp?.Port ?? 0,
                Quirks = account.Quirks.Latched,
                FolderCount = folders.Count,
                UnreadCount = unread,
            });
        }

        return summaries;
    }

    public AccountSummary? Get(AccountId accountId, CancellationToken ct = default)
    {
        foreach (var summary in List(ct))
            if (summary.Id == accountId) return summary;

        return null;
    }

    /// <summary>Reconciles folders and syncs every one of them, reporting progress as it goes.</summary>
    public Task<SyncReport> InitialSyncAsync(
        IMailProvider provider,
        AccountId accountId,
        IProgress<SyncProgress>? progress,
        CancellationToken ct) =>
        _sync.SyncAccountAsync(provider, accountId, progress, ct);

    /// <summary>Derives a keyring-safe handle from the address; the charset is deliberately narrow.</summary>
    public static string DeriveSecretRef(EmailAddress email)
    {
        var value = email.Value;
        var builder = new StringBuilder(SecretRefPrefix.Length + value.Length);
        builder.Append(SecretRefPrefix);

        foreach (var c in value)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                     || c is '.' or '-' or '_' or ':' or '@' or '+';
            builder.Append(ok ? c : '_');
            if (builder.Length >= 128) break;
        }

        return builder.ToString();
    }
}
