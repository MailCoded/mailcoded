using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Mcp;

/// <summary>Connect-on-demand IMAP and SMTP adapters, owned by the composition root.</summary>
internal sealed class MailConnections : IAsyncDisposable
{
    private readonly SqliteStore _store;
    private readonly ISecretStore _secrets;
    private readonly ConnectionRegistry _registry;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, ImapProvider> _providers = [];
    private readonly Dictionary<long, SmtpSender> _senders = [];
    private bool _disposed;

    public MailConnections(SqliteStore store, ISecretStore secrets, ConnectionRegistry registry, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _secrets = secrets;
        _registry = registry;
        _clock = clock;
    }

    public async Task<IMailProvider> ProviderAsync(AccountId accountId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var config = RequireAccount(accountId, ct);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_providers.TryGetValue(accountId.Value, out var existing))
            {
                if (existing.IsConnected) return existing;
                _providers.Remove(accountId.Value);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var provider = new ImapProvider(_clock);
            _registry.Observe(accountId, ConnectionRole.Imap, ConnectionState.Connecting);

            try
            {
                await provider.ConnectAsync(config, _secrets, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _registry.Observe(accountId, ConnectionRole.Imap, StateFor(ex));
                await provider.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _registry.Observe(accountId, ConnectionRole.Imap, ConnectionState.Connected);
            _providers[accountId.Value] = provider;
            return provider;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Null when the server is unreachable, so a local-only operation can still proceed.</summary>
    public async Task<IMailProvider?> TryProviderAsync(AccountId accountId, CancellationToken ct)
    {
        try
        {
            return await ProviderAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            return null;
        }
        catch (SecretStoreException)
        {
            return null;
        }
    }

    public async Task<IMailSender> SenderAsync(AccountId accountId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var config = RequireAccount(accountId, ct);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_senders.TryGetValue(accountId.Value, out var existing))
            {
                if (existing.IsConnected) return existing;
                _senders.Remove(accountId.Value);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var sender = new SmtpSender(_clock);
            _registry.Observe(accountId, ConnectionRole.Smtp, ConnectionState.Connecting);

            try
            {
                await sender.ConnectAsync(config, _secrets, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _registry.Observe(accountId, ConnectionRole.Smtp, StateFor(ex));
                await sender.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _registry.Observe(accountId, ConnectionRole.Smtp, ConnectionState.Connected);
            _senders[accountId.Value] = sender;
            return sender;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var provider in _providers.Values) await provider.DisposeAsync().ConfigureAwait(false);
        foreach (var sender in _senders.Values) await sender.DisposeAsync().ConfigureAwait(false);

        _providers.Clear();
        _senders.Clear();
        _gate.Dispose();
    }

    private AccountConfig RequireAccount(AccountId accountId, CancellationToken ct) =>
        _store.GetAccount(accountId, ct)
        ?? throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

    private static ConnectionState StateFor(Exception ex) =>
        ex is ProviderException { Category: FailureCategory.Auth } ? ConnectionState.AuthRequired : ConnectionState.Error;
}
