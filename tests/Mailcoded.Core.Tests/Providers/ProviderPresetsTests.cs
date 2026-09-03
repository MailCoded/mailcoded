using Mailcoded.Core.Providers;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

public sealed class ProviderPresetsTests
{
    [Theory]
    [InlineData("me@gmail.com", "imap.gmail.com", "smtp.gmail.com")]
    [InlineData("me@googlemail.com", "imap.gmail.com", "smtp.gmail.com")]
    [InlineData("me@yahoo.com", "imap.mail.yahoo.com", "smtp.mail.yahoo.com")]
    [InlineData("me@icloud.com", "imap.mail.me.com", "smtp.mail.me.com")]
    [InlineData("me@me.com", "imap.mail.me.com", "smtp.mail.me.com")]
    [InlineData("me@fastmail.com", "imap.fastmail.com", "smtp.fastmail.com")]
    [InlineData("me@qq.com", "imap.qq.com", "smtp.qq.com")]
    [InlineData("me@163.com", "imap.163.com", "smtp.163.com")]
    public void Known_providers_resolve_to_their_documented_hosts(string email, string imap, string smtp)
    {
        var preset = ProviderPresets.ForEmail(email);

        Assert.Equal(imap, preset.ImapHost);
        Assert.Equal(smtp, preset.SmtpHost);
        Assert.False(preset.IsGuess, $"{email} should resolve from the built-in table, not a guess.");
    }

    [Fact]
    public void An_unknown_domain_is_guessed_from_the_name_and_says_so()
    {
        var preset = ProviderPresets.ForEmail("someone@totally-unknown-host.example");

        Assert.True(preset.IsGuess);
        Assert.Equal("imap.totally-unknown-host.example", preset.ImapHost);
        Assert.Equal("smtp.totally-unknown-host.example", preset.SmtpHost);
        Assert.NotNull(preset.Advice);
    }

    [Fact]
    public void A_subdomain_inherits_its_parent_provider()
    {
        // Business mail on a provider subdomain should not fall through to a guess.
        var preset = ProviderPresets.ForDomain("mail.gmail.com");

        Assert.Equal("imap.gmail.com", preset.ImapHost);
        Assert.False(preset.IsGuess);
    }

    [Theory]
    [InlineData("me@outlook.com")]
    [InlineData("me@hotmail.com")]
    [InlineData("me@live.com")]
    [InlineData("me@msn.com")]
    public void Microsoft_accounts_are_warned_about_but_not_blocked(string email)
    {
        var preset = ProviderPresets.ForEmail(email);

        Assert.Equal(CredentialStyle.OAuthOnly, preset.Credential);

        // A warning, not a verdict: the table describes the provider's usual policy, and cannot
        // know whether this particular account still accepts an app password.
        Assert.False(string.IsNullOrWhiteSpace(preset.Discouraged));
        Assert.DoesNotContain("cannot work", preset.Discouraged!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("me@gmail.com")]
    [InlineData("me@yahoo.com")]
    [InlineData("me@icloud.com")]
    [InlineData("me@fastmail.com")]
    [InlineData("me@qq.com")]
    public void Providers_that_reject_account_passwords_say_so_before_one_is_typed(string email)
    {
        var preset = ProviderPresets.ForEmail(email);

        Assert.Equal(CredentialStyle.AppPassword, preset.Credential);
        Assert.False(
            string.IsNullOrWhiteSpace(preset.Advice),
            "A provider that refuses account passwords must explain that before the prompt.");
    }

    [Fact]
    public void Proton_points_at_the_local_bridge_not_a_public_host()
    {
        var preset = ProviderPresets.ForEmail("me@proton.me");

        Assert.Equal("127.0.0.1", preset.ImapHost);
        Assert.Equal(1143, preset.ImapPort);
        Assert.Contains("Bridge", preset.Advice ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_preset_is_internally_consistent()
    {
        foreach (var domain in ProviderPresets.KnownDomains())
        {
            var preset = ProviderPresets.ForDomain(domain);

            Assert.False(string.IsNullOrWhiteSpace(preset.DisplayName), domain);
            Assert.False(string.IsNullOrWhiteSpace(preset.ImapHost), domain);
            Assert.False(string.IsNullOrWhiteSpace(preset.SmtpHost), domain);
            Assert.InRange(preset.ImapPort, 1, 65535);
            Assert.InRange(preset.SmtpPort, 1, 65535);
            Assert.False(preset.IsGuess, $"{domain} is in the table, so it is not a guess.");

            // Plaintext is only ever defensible against the loopback bridge.
            if (preset.ImapSecurity == SecureSocket.None)
                Assert.StartsWith("127.0.0.1", preset.ImapHost, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    [InlineData("trailing@")]
    public void A_malformed_address_is_rejected_rather_than_guessed(string email) =>
        Assert.Throws<ArgumentException>(() => ProviderPresets.ForEmail(email));

    [Fact]
    public void Lookup_is_case_insensitive() =>
        Assert.Equal(
            ProviderPresets.ForEmail("me@gmail.com").ImapHost,
            ProviderPresets.ForEmail("Me@GMAIL.COM").ImapHost);
}
