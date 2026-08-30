using System.Collections.Concurrent;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Daemon;

/// <summary>
/// Owns the concrete transport adapters. One <see cref="ImapProvider"/> per account serves every
/// on-demand command (it serializes internally) and holds the IDLE watchers; one
/// <see cref="SmtpSender"/> per account serves submissions. Connection state is mirrored into
/// <see cref="ConnectionRegistry"/> so <c>health</c> answers without touching the network.
/// </summary>
internal sealed class ProviderPool : IAsyncDisposable
{
    private readonly SqliteStore store;
    private readonly ISecretStore secrets;
    private readonly ConnectionRegistry connections;
    private readonly MailTransportOptions options;
    private readonly IClock clock;
    private readonly StderrLog log;
    private readonly ConcurrentDictionary<long, AccountTransports> accounts = new();
    private int disposed;

    public ProviderPool(
        SqliteStore store,
        ISecretStore secrets,
        ConnectionRegistry connections,
        MailTransportOptions options,
        IClock clock,
        StderrLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        this.store = store;
        this.secrets = secrets;
        this.connections = connections;
        this.options = options;
        this.clock = clock;
        this.log = log;
    }

    public async Task<IMailProvider> GetProviderAsync(AccountId accountId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var config = RequireAccount(accountId, ct);
        var slot = accounts.GetOrAdd(accountId.Value, _ => new AccountTransports());

        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (slot.Imap is { IsConnected: true } live) return live;

            var provider = slot.Imap;
            if (provider is null)
            {
                provider = new ImapProvider(clock, options);
                slot.Imap = provider;
            }

            connections.Observe(accountId, ConnectionRole.Imap, ConnectionState.Connecting);

            try
            {
                await provider.ConnectAsync(config, secrets, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(accountId, ConnectionRole.Imap, ex);
                throw;
            }

            connections.Observe(accountId, ConnectionRole.Imap, ConnectionState.Connected);
            return provider;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task<IMailSender> GetSenderAsync(AccountId accountId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var config = RequireAccount(accountId, ct);
        if (config.Smtp is null)
        {
            throw new ProviderException(
                FailureCategory.Unsupported,
                $"Account {accountId.Value} has no SMTP configuration, so it cannot send.");
        }

        var slot = accounts.GetOrAdd(accountId.Value, _ => new AccountTransports());

        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (slot.Smtp is { IsConnected: true } live) return live;

            var sender = slot.Smtp;
            if (sender is null)
            {
                sender = new SmtpSender(clock, options);
                slot.Smtp = sender;
            }

            connections.Observe(accountId, ConnectionRole.Smtp, ConnectionState.Connecting);

            try
            {
                await sender.ConnectAsync(config, secrets, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(accountId, ConnectionRole.Smtp, ex);
                throw;
            }

            connections.Observe(accountId, ConnectionRole.Smtp, ConnectionState.Connected);
            return sender;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Best-effort provider for an operation that degrades to local-only when offline.</summary>
    public async Task<IMailProvider?> TryGetProviderAsync(AccountId accountId, CancellationToken ct)
    {
        try
        {
            return await GetProviderAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ProviderException or SecretStoreException)
        {
            log.Warn($"Account {accountId.Value} is unreachable; continuing against the local store only.");
            return null;
        }
    }

    private AccountConfig RequireAccount(AccountId accountId, CancellationToken ct) =>
        store.GetAccount(accountId, ct)
        ?? throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

    private void Report(AccountId accountId, ConnectionRole role, Exception exception)
    {
        var state = exception is ProviderException { Category: FailureCategory.Auth } or SecretStoreException
            ? ConnectionState.AuthRequired
            : ConnectionState.Error;

        connections.Observe(accountId, role, state, exception.Message);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        foreach (var (id, slot) in accounts)
        {
            try
            {
                if (slot.Imap is { } provider) await provider.DisposeAsync().ConfigureAwait(false);
                if (slot.Smtp is { } sender) await sender.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Debug($"Disposing transports for account {id} failed: {ex.Message}");
            }
            finally
            {
                slot.Gate.Dispose();
                connections.Forget(new AccountId(id));
            }
        }

        accounts.Clear();
    }

    private sealed class AccountTransports
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ImapProvider? Imap { get; set; }

        public SmtpSender? Smtp { get; set; }
    }
}
