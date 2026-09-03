using System.Collections.Concurrent;
using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Auth;
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
            if (slot.Imap is { IsConnected: true } live)
            {
                slot.ImapPolicy?.RecordSuccess();
                return live;
            }

            // Every IDLE burst lands here, so a dead credential or a dead network would otherwise
            // mean one full TCP+TLS+AUTHENTICATE per signal (RELIABILITY §14.4).
            ThrowIfCoolingDown(accountId, slot);

            var provider = slot.Imap;
            if (provider is null)
            {
                provider = new ImapProvider(clock, options, AccountTokenSources.For(config, secrets));
                slot.Imap = provider;
            }

            connections.Observe(accountId, ConnectionRole.Imap, ConnectionState.Connecting);

            try
            {
                await provider.ConnectAsync(config, secrets, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report(accountId, ConnectionRole.Imap, ex);
                throw StartCooldown(accountId, slot, ex);
            }

            slot.ImapCooldownUntilTicks = 0;
            slot.ImapCooldownCategory = null;
            slot.ImapPolicy?.RecordSuccess();
            connections.Observe(accountId, ConnectionRole.Imap, ConnectionState.Connected);
            return provider;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Refuses the connect attempt while the account's reconnect backoff is still running.</summary>
    private void ThrowIfCoolingDown(AccountId accountId, AccountTransports slot)
    {
        if (slot.ImapCooldownCategory is not { } category) return;

        var remaining = slot.ImapCooldownUntilTicks - clock.Ticks;
        if (remaining <= 0)
        {
            slot.ImapCooldownCategory = null;
            return;
        }

        throw category == FailureCategory.Auth
            ? new ProviderException(
                FailureCategory.Auth,
                $"Account {accountId.Value} needs re-authorization before it can connect again ({RetryDecision.AuthRequired}).")
            : new ProviderException(
                FailureCategory.Network,
                $"Account {accountId.Value} is in reconnect backoff for another {remaining.ToString(CultureInfo.InvariantCulture)} ms.");
    }

    /// <summary>
    /// Arms the backoff and returns what the caller should throw. A hard AUTHENTICATIONFAILED is
    /// counted separately and surfaces as <c>auth-required</c> rather than being retried at speed.
    /// </summary>
    private Exception StartCooldown(AccountId accountId, AccountTransports slot, Exception failure)
    {
        var policy = slot.ImapPolicy ??= NewPolicy();
        var category = Categorize(failure);
        var decision = policy.OnFailure(category);

        var cooldownMs = decision.ShouldRetry
            ? (long)decision.Delay.TotalMilliseconds
            : ReconnectPolicy.DefaultMaxDelayMs;

        slot.ImapCooldownUntilTicks = clock.Ticks + cooldownMs;
        slot.ImapCooldownCategory = category;

        if (!decision.RequiresUserAction) return failure;

        log.Warn($"Account {accountId.Value} needs re-authorization; reconnects are paused ({RetryDecision.AuthRequired}).");
        return new ProviderException(
            FailureCategory.Auth,
            $"Account {accountId.Value} needs re-authorization ({RetryDecision.AuthRequired}).",
            failure);
    }

    private ReconnectPolicy NewPolicy() =>
        new(clock, options.RandomSeed is { } seed ? new Random(seed) : Random.Shared);

    private static FailureCategory Categorize(Exception failure) => failure switch
    {
        ProviderException provider => provider.Category,
        SecretStoreException => FailureCategory.Auth,
        _ => FailureCategory.Network,
    };

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
                sender = new SmtpSender(clock, options, AccountTokenSources.For(config, secrets));
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

        public ReconnectPolicy? ImapPolicy { get; set; }

        /// <summary>Monotonic deadline; wall clock is never a basis for an interval (§14.4).</summary>
        public long ImapCooldownUntilTicks { get; set; }

        public FailureCategory? ImapCooldownCategory { get; set; }
    }
}
