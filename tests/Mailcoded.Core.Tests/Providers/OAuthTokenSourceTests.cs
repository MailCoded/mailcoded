using System.Reflection;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

/// <summary>An OAuth grant lives under its own key, so a connection built without a token source
/// looks up the account's secret ref and finds nothing there. That is not "no token stored".</summary>
public sealed class OAuthTokenSourceTests
{
    [Fact]
    public void An_oauth_account_is_given_a_token_source()
    {
        var source = AccountTokenSources.For(Account(AuthKind.OAuth2), Secrets);

        Assert.NotNull(source);
    }

    [Fact]
    public void A_password_account_is_given_none_because_it_needs_none()
    {
        var source = AccountTokenSources.For(Account(AuthKind.Password), Secrets);

        Assert.Null(source);
    }

    /// <summary>The grant and the account credential are different keys; conflating them is the bug.</summary>
    [Fact]
    public void The_token_cache_key_is_not_the_account_secret_ref()
    {
        const string SecretRef = "acct:me@example.test:oauth";

        Assert.NotEqual(SecretRef, OAuthOptions.CacheRefFor(SecretRef));
        Assert.StartsWith(SecretRef, OAuthOptions.CacheRefFor(SecretRef), StringComparison.Ordinal);
    }

    /// <summary>Every place that opens a mail connection must pass one, or OAuth silently cannot
    /// authenticate anywhere except the two commands that build their own.</summary>
    [Theory]
    [InlineData("Mailcoded.Cli", "CliHost.cs")]
    [InlineData("Mailcoded.Daemon", "ProviderPool.cs")]
    [InlineData("Mailcoded.Mcp", "MailConnections.cs")]
    public void Every_host_that_builds_a_transport_supplies_a_token_source(string project, string file)
    {
        var source = File.ReadAllText(RepositoryFile(Path.Combine("src", project, file)));

        foreach (var construction in new[] { "new ImapProvider(", "new SmtpSender(" })
        {
            if (!source.Contains(construction, StringComparison.Ordinal)) continue;

            Assert.Contains(
                "AccountTokenSources.For(",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_failure_text_distinguishes_a_missing_source_from_a_refused_renewal()
    {
        foreach (var file in new[]
        {
            RepositoryFile("src/Mailcoded.Core/Providers/ImapConnectionFactory.cs"),
            RepositoryFile("src/Mailcoded.Core/Providers/SmtpSender.cs"),
        })
        {
            var source = File.ReadAllText(file);

            Assert.Contains("without a token source", source, StringComparison.Ordinal);
            Assert.Contains("account reauth", source, StringComparison.Ordinal);
            Assert.DoesNotContain("No OAuth2 access token is stored", source, StringComparison.Ordinal);
        }
    }

    private static readonly ISecretStore Secrets = new EncryptedFileStore(
        Path.Combine(Path.GetTempPath(), "mailcoded-tokensource-tests"));

    private static AccountConfig Account(AuthKind auth) =>
        StoreSeed.AccountConfigFor("me@example.test") with { Auth = auth, SecretRef = "acct:me@example.test:x" };

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"No '{relative}' above '{AppContext.BaseDirectory}'.");
    }
}
