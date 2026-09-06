using System.Diagnostics;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

/// <summary>An account with no credential can only ever reach AUTHENTICATE and fail, so the wait is
/// spent before the socket. The import-eml placeholder points at localhost:993 and has none, which
/// otherwise cost a full connect timeout on every sync.</summary>
public sealed class MissingCredentialTests
{
    private static AccountConfig Account(AuthKind auth = AuthKind.Password) =>
        StoreSeed.AccountConfigFor("nobody@example.test") with
        {
            Auth = auth,
            SecretRef = "imap:nobody@example.test",
            Imap = new ImapConfig { Host = "localhost", Port = 1, Security = SecureSocket.None },
        };

    [Fact]
    public async Task Connecting_without_a_credential_fails_as_auth_not_network()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var provider = new ImapProvider(new TestClock(), MailTransportOptions.Default, null);

        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ConnectAsync(Account(), new EmptySecretStore(), ct));

        Assert.Equal(FailureCategory.Auth, failure.Category);
        Assert.Contains("nobody@example.test", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The point of the guard: it must not wait out a connect timeout first.</summary>
    [Fact]
    public async Task It_refuses_immediately_rather_than_waiting_for_the_socket()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = MailTransportOptions.Default with { ConnectTimeoutMs = 30_000 };
        await using var provider = new ImapProvider(new TestClock(), options, null);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ConnectAsync(Account(), new EmptySecretStore(), ct));

        Assert.True(watch.ElapsedMilliseconds < 2_000, $"took {watch.ElapsedMilliseconds} ms; the guard did not run first");
    }

    /// <summary>An OAuth account keeps its grant in the token cache beside the ref, never under the ref.</summary>
    [Fact]
    public async Task An_oauth_grant_counts_as_a_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = Account(AuthKind.OAuth2);
        var secrets = new EmptySecretStore();

        Assert.False(await AccountCredentials.ExistsAsync(account, secrets, ct));

        await secrets.SetAsync(OAuthOptions.CacheRefFor(account.SecretRef), "{}", ct);

        Assert.True(await AccountCredentials.ExistsAsync(account, secrets, ct));
        Assert.False(await AccountCredentials.ExistsAsync(account with { Auth = AuthKind.Password }, secrets, ct));
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string BackendName => "test-empty";

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
