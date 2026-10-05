using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

public sealed class AuthservPresetTests
{
    [Fact]
    public void Only_gmail_ships_a_trusted_authserv_id()
    {
        var withTrust = new List<string>();

        foreach (var domain in ProviderPresets.KnownDomains())
        {
            var preset = ProviderPresets.ForDomain(domain);
            if (preset.TrustedAuthservIds.Count > 0) withTrust.Add(domain);
        }

        Assert.Equal(["gmail.com", "googlemail.com"], withTrust.Order(StringComparer.Ordinal));
        Assert.Equal(["mx.google.com"], ProviderPresets.ForEmail("me@gmail.com").TrustedAuthservIds);
        Assert.Empty(ProviderPresets.ForEmail("me@totally-unknown-host.example").TrustedAuthservIds);
    }

    [Theory]
    [InlineData("imap.gmail.com", true)]
    [InlineData("IMAP.GMAIL.COM", true)]
    [InlineData("imap.gmail.com.", true)]
    [InlineData("imap.gmail.com.evil.example.org", false)]
    [InlineData("evil-imap.gmail.com", false)]
    [InlineData("imap.fastmail.com", false)]
    [InlineData("outlook.office365.com", false)]
    [InlineData("imap.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_trust_follows_the_imap_host_so_a_custom_domain_on_gmail_inherits_it(string? host, bool trusted)
    {
        var trust = ProviderPresets.TrustedAuthservIdsForImapHost(host);

        Assert.Equal(trusted, trust.Matches("mx.google.com"));
        Assert.False(trust.Matches("mx.example.net"));
    }

    [Fact]
    public void The_effective_trust_is_the_providers_plus_what_the_human_added()
    {
        var gmail = StoreSeed.AccountConfigFor("me@example.com") with
        {
            Imap = new ImapConfig { Host = "imap.gmail.com" },
            TrustedAuthservIds = ["mx.example.net"],
        };

        var trust = gmail.EffectiveAuthservTrust();
        Assert.True(trust.Matches("mx.google.com"));
        Assert.True(trust.Matches("mx.example.net"));
        Assert.False(trust.Matches("mx.example.org"));

        var plain = StoreSeed.AccountConfigFor("me@example.com");
        Assert.True(plain.EffectiveAuthservTrust().IsEmpty);
        Assert.True((plain with { TrustedAuthservIds = ["mx.example.net"] }).EffectiveAuthservTrust().Matches("mx.example.net"));
    }
}
