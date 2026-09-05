using Mailcoded.Core.Application;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Accounts;

/// <summary>An OAuth account keeps no password, so its credential is the token cache beside the ref.
/// Looking only at the ref reports every signed-in OAuth account as having nothing stored.</summary>
public sealed class CredentialLookupTests
{
    [Fact]
    public async Task APasswordAccountIsFoundByItsOwnRef()
    {
        var ct = TestContext.Current.CancellationToken;
        using var rig = Rig.Create();

        var id = await rig.AddAsync(AuthKind.Password, "ref:pw", ct);
        await rig.Secrets.SetAsync("ref:pw", "hunter2", ct);

        Assert.True(await rig.Accounts.HasCredentialAsync(id, ct));
    }

    [Fact]
    public async Task AnOAuthAccountIsFoundByItsTokenCache()
    {
        var ct = TestContext.Current.CancellationToken;
        using var rig = Rig.Create();

        var id = await rig.AddAsync(AuthKind.OAuth2, "ref:oauth", ct);
        await rig.Secrets.SetAsync(OAuthOptions.CacheRefFor("ref:oauth"), "{}", ct);

        Assert.True(await rig.Accounts.HasCredentialAsync(id, ct));
    }

    /// <summary>The bare ref of an OAuth account is always empty; that must not read as credentialed.</summary>
    [Fact]
    public async Task AnOAuthAccountWithOnlyABareRefIsNotCredentialed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var rig = Rig.Create();

        var id = await rig.AddAsync(AuthKind.OAuth2, "ref:oauth", ct);
        await rig.Secrets.SetAsync("ref:oauth", "not-a-token-cache", ct);

        Assert.False(await rig.Accounts.HasCredentialAsync(id, ct));
    }

    [Fact]
    public async Task AnImportPlaceholderWithNothingStoredHasNoCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        using var rig = Rig.Create();

        var id = await rig.AddAsync(AuthKind.Password, "ref:none", ct);

        Assert.False(await rig.Accounts.HasCredentialAsync(id, ct));
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempStore _temp;

        private Rig(TempStore temp, AccountService accounts, ISecretStore secrets)
        {
            _temp = temp;
            Accounts = accounts;
            Secrets = secrets;
        }

        public AccountService Accounts { get; }

        public ISecretStore Secrets { get; }

        public static Rig Create()
        {
            var temp = TempStore.Create();
            var secrets = new MemorySecretStore();
            var audit = new AuditLog(temp.Store, temp.Clock);
            var sync = new SyncEngine(temp.Store, ReferencesThreader.Instance, temp.Clock, audit);

            return new Rig(temp, new AccountService(temp.Store, secrets, sync, audit), secrets);
        }

        public Task<AccountId> AddAsync(AuthKind auth, string secretRef, CancellationToken ct) =>
            _temp.Store.AddAccountAsync(
                StoreSeed.AccountConfigFor("me@example.test") with { Auth = auth, SecretRef = secretRef },
                ct);

        public void Dispose() => _temp.Dispose();
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string BackendName => "test-memory";

        public bool IsAvailable => true;

        public Task SetAsync(string secretRef, string value, CancellationToken ct)
        {
            _values[secretRef] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string secretRef, CancellationToken ct) =>
            Task.FromResult(_values.TryGetValue(secretRef, out var value) ? value : null);

        public Task DeleteAsync(string secretRef, CancellationToken ct)
        {
            _values.Remove(secretRef);
            return Task.CompletedTask;
        }
    }
}
