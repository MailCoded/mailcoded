using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class AccountStoreTests
{
    private static readonly string[] AllowedJsonProperties =
    [
        "version", "secretRef", "auth",
        "imap", "smtp", "quirks",
        "host", "port", "security", "username", "watchFolders",
        "latched", "pinnedCertificateSha256",
    ];

    private static AccountConfig FullConfig(string email = "tester@example.com") => new()
    {
        Email = email,
        DisplayName = "Test Account",
        Provider = ProviderKind.Imap,
        Imap = new ImapConfig
        {
            Host = "imap.example.com",
            Port = 1993,
            Security = SecureSocket.StartTls,
            Username = "imap-user",
            WatchFolders = ["INBOX", "Archive/2026"],
        },
        Smtp = new SmtpConfig
        {
            Host = "smtp.example.com",
            Port = 2587,
            Security = SecureSocket.StartTlsWhenAvailable,
            Username = "smtp-user",
        },
        Auth = AuthKind.OAuth2,
        SecretRef = "mailcoded:oauth:tester@example.com",
        Quirks = new ServerQuirksConfig
        {
            Latched = ServerQuirks.QresyncBroken | ServerQuirks.RequiresId,
            PinnedCertificateSha256 = new string('a', 64),
        },
    };

    [Fact]
    public async Task AccountRoundTripsThroughConfigJson()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var id = await temp.Store.AddAccountAsync(FullConfig(), ct);
        var loaded = Require.Ref(temp.Store.GetAccount(id, ct), "the stored account");

        Assert.Equal(id, loaded.Id);
        Assert.Equal("tester@example.com", loaded.Email);
        Assert.Equal("Test Account", loaded.DisplayName);
        Assert.Equal(ProviderKind.Imap, loaded.Provider);
        Assert.Equal(AuthKind.OAuth2, loaded.Auth);
        Assert.Equal("mailcoded:oauth:tester@example.com", loaded.SecretRef);

        Assert.Equal("imap.example.com", loaded.Imap.Host);
        Assert.Equal(1993, loaded.Imap.Port);
        Assert.Equal(SecureSocket.StartTls, loaded.Imap.Security);
        Assert.Equal("imap-user", loaded.Imap.Username);
        Assert.Equal(new[] { "INBOX", "Archive/2026" }, loaded.Imap.WatchFolders);

        var smtp = Require.Ref(loaded.Smtp, "the stored SMTP config");
        Assert.Equal("smtp.example.com", smtp.Host);
        Assert.Equal(2587, smtp.Port);
        Assert.Equal(SecureSocket.StartTlsWhenAvailable, smtp.Security);
        Assert.Equal("smtp-user", smtp.Username);

        Assert.Equal(ServerQuirks.QresyncBroken | ServerQuirks.RequiresId, loaded.Quirks.Latched);
        Assert.Equal(new string('a', 64), loaded.Quirks.PinnedCertificateSha256);
    }

    [Fact]
    public async Task AddingTheSameEmailUpdatesRatherThanDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var first = await temp.Store.AddAccountAsync(FullConfig(), ct);
        var second = await temp.Store.AddAccountAsync(
            FullConfig() with { DisplayName = "Renamed", Provider = ProviderKind.Gmail },
            ct);

        Assert.Equal(first, second);
        Assert.Single(temp.Store.ListAccounts(ct));

        var loaded = Require.Ref(temp.Store.GetAccount(first, ct), "the updated account");
        Assert.Equal("Renamed", loaded.DisplayName);
        Assert.Equal(ProviderKind.Gmail, loaded.Provider);
    }

    [Fact]
    public async Task UpdateRewritesEveryPersistedField()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var id = await temp.Store.AddAccountAsync(FullConfig(), ct);
        var updated = FullConfig("moved@example.org") with
        {
            Id = id,
            Auth = AuthKind.Password,
            SecretRef = "mailcoded:password:moved@example.org",
            Smtp = null,
        };

        await temp.Store.UpdateAccountAsync(updated, ct);

        var loaded = Require.Ref(temp.Store.FindAccountByEmail("moved@example.org", ct), "the renamed account");
        Assert.Equal(id, loaded.Id);
        Assert.Equal(AuthKind.Password, loaded.Auth);
        Assert.Equal("mailcoded:password:moved@example.org", loaded.SecretRef);
        Assert.Null(loaded.Smtp);
        Assert.Null(temp.Store.FindAccountByEmail("tester@example.com", ct));
    }

    [Fact]
    public async Task UpdatingAnUnknownAccountIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var missing = FullConfig() with { Id = new AccountId(4242) };
        var failure = await Assert.ThrowsAsync<StoreException>(() => temp.Store.UpdateAccountAsync(missing, ct));

        Assert.Equal(FailureCategory.NotFound, failure.Category);
    }

    [Fact]
    public async Task ConfigJsonCarriesOnlyAllowlistedProperties()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        await temp.Store.AddAccountAsync(FullConfig(), ct);
        var json = Assert.Single(StoreQuery.Strings(temp.Store, "SELECT config_json FROM accounts", ct));

        using var document = JsonDocument.Parse(json);
        var names = new List<string>();
        CollectPropertyNames(document.RootElement, names);

        foreach (var name in names)
        {
            Assert.Contains(name, AllowedJsonProperties);
            Assert.False(LooksLikeACredential(name), $"config_json grew a credential-shaped field '{name}'.");
        }

        Assert.Contains("\"secretRef\":\"mailcoded:oauth:tester@example.com\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NoConfigTypeExposesACredentialBearingProperty()
    {
        Type[] persisted = [typeof(AccountConfig), typeof(ImapConfig), typeof(SmtpConfig), typeof(ServerQuirksConfig)];

        foreach (var type in persisted)
        {
            foreach (var property in type.GetProperties())
            {
                Assert.False(
                    LooksLikeACredential(property.Name),
                    $"{type.Name}.{property.Name} is credential-shaped and would be serialized into accounts.config_json.");
            }
        }
    }

    [Fact]
    public void TheAccountsTableHasNoColumnThatCouldHoldACredential()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var columns = StoreQuery.ColumnNames(temp.Store, "accounts", ct);
        Assert.Equal(new[] { "config_json", "display_name", "email", "id", "provider" }, columns);
    }

    private static bool LooksLikeACredential(string name)
    {
        if (name.Equals("secretRef", StringComparison.OrdinalIgnoreCase)) return false;

        string[] markers = ["password", "passwd", "secret", "token", "credential", "apikey", "accesskey", "refresh"];
        foreach (var marker in markers)
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static void CollectPropertyNames(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    CollectPropertyNames(property.Value, names);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectPropertyNames(item, names);
                break;

            default:
                break;
        }
    }
}
