namespace Mailcoded.Core.Providers;

/// <summary>How a provider expects to be authenticated, and what to tell the human about it.</summary>
public enum CredentialStyle
{
    /// <summary>An ordinary account password works.</summary>
    Password,

    /// <summary>A provider-generated app password. The account password will be rejected.</summary>
    AppPassword,

    /// <summary>Only OAuth2 works — basic authentication is switched off server-side.</summary>
    OAuthOnly,
}

/// <summary>
/// Connection settings for a known mail provider, plus the guidance a first-time user needs.
/// Pure data and pure lookup — no I/O, no network probe — so onboarding in any client (CLI today,
/// the editor extension later) resolves a domain the same way.
/// </summary>
public sealed record ProviderPreset
{
    public required string DisplayName { get; init; }
    public required string ImapHost { get; init; }
    public int ImapPort { get; init; } = 993;
    public SecureSocket ImapSecurity { get; init; } = SecureSocket.SslOnConnect;

    public required string SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public SecureSocket SmtpSecurity { get; init; } = SecureSocket.StartTls;

    public CredentialStyle Credential { get; init; } = CredentialStyle.Password;

    /// <summary>Where the human goes to create the credential. Null when an ordinary password works.</summary>
    public string? CredentialUrl { get; init; }

    /// <summary>One line the human must read before typing a credential. Null when there is nothing to say.</summary>
    public string? Advice { get; init; }

    /// <summary>True when this came from a name guess rather than a known provider.</summary>
    public bool IsGuess { get; init; }

    /// <summary>
    /// Set when this provider is expected to refuse an ordinary password. User-facing, and a
    /// warning rather than a verdict: it is a statement about the provider's usual policy, not
    /// about this specific account, so a client should still let the human try.
    /// </summary>
    public string? Discouraged { get; init; }
}

/// <summary>Domain to <see cref="ProviderPreset"/>. Add a provider here, not in a client.</summary>
public static class ProviderPresets
{
    private const string GmailAdvice =
        "Gmail rejects your Google password over IMAP. Create a 16-character app password "
        + "(2-Step Verification must be on first).";

    private const string MicrosoftAdvice =
        "Microsoft has been withdrawing basic authentication for Outlook.com and Microsoft 365, so "
        + "an ordinary password is likely to be refused. If your account still has app passwords "
        + "enabled, one may work. Signing in with Microsoft avoids the problem.";

    private static readonly Dictionary<string, ProviderPreset> ByDomain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = Gmail("Gmail"),
        ["googlemail.com"] = Gmail("Gmail"),

        ["outlook.com"] = Microsoft("Outlook.com"),
        ["hotmail.com"] = Microsoft("Outlook.com"),
        ["hotmail.co.uk"] = Microsoft("Outlook.com"),
        ["live.com"] = Microsoft("Outlook.com"),
        ["live.co.uk"] = Microsoft("Outlook.com"),
        ["msn.com"] = Microsoft("Outlook.com"),
        ["office365.com"] = Microsoft("Microsoft 365"),

        ["yahoo.com"] = Yahoo("Yahoo Mail"),
        ["yahoo.co.uk"] = Yahoo("Yahoo Mail"),
        ["yahoo.co.jp"] = Yahoo("Yahoo Mail"),
        ["ymail.com"] = Yahoo("Yahoo Mail"),
        ["rocketmail.com"] = Yahoo("Yahoo Mail"),
        ["aol.com"] = new ProviderPreset
        {
            DisplayName = "AOL Mail",
            ImapHost = "imap.aol.com",
            SmtpHost = "smtp.aol.com",
            SmtpPort = 465,
            SmtpSecurity = SecureSocket.SslOnConnect,
            Credential = CredentialStyle.AppPassword,
            CredentialUrl = "https://login.aol.com/account/security",
            Advice = "AOL requires an app password generated from account security settings.",
        },

        ["icloud.com"] = Icloud(),
        ["me.com"] = Icloud(),
        ["mac.com"] = Icloud(),

        ["fastmail.com"] = Fastmail(),
        ["fastmail.fm"] = Fastmail(),

        ["zoho.com"] = new ProviderPreset
        {
            DisplayName = "Zoho Mail",
            ImapHost = "imap.zoho.com",
            SmtpHost = "smtp.zoho.com",
            SmtpPort = 465,
            SmtpSecurity = SecureSocket.SslOnConnect,
            Credential = CredentialStyle.AppPassword,
            CredentialUrl = "https://accounts.zoho.com/home#security/app_passwords",
            Advice = "Zoho requires an application-specific password when two-factor is enabled.",
        },

        ["gmx.com"] = Gmx("GMX"),
        ["gmx.net"] = Gmx("GMX"),
        ["gmx.de"] = Gmx("GMX"),
        ["web.de"] = new ProviderPreset
        {
            DisplayName = "WEB.DE",
            ImapHost = "imap.web.de",
            SmtpHost = "smtp.web.de",
            Advice = "IMAP must be enabled in the WEB.DE web interface before it will accept a login.",
        },

        ["yandex.com"] = Yandex(),
        ["yandex.ru"] = Yandex(),

        ["mail.ru"] = new ProviderPreset
        {
            DisplayName = "Mail.ru",
            ImapHost = "imap.mail.ru",
            SmtpHost = "smtp.mail.ru",
            SmtpPort = 465,
            SmtpSecurity = SecureSocket.SslOnConnect,
            Credential = CredentialStyle.AppPassword,
            Advice = "Mail.ru requires an app password created in account security settings.",
        },

        ["qq.com"] = new ProviderPreset
        {
            DisplayName = "QQ Mail",
            ImapHost = "imap.qq.com",
            SmtpHost = "smtp.qq.com",
            SmtpPort = 465,
            SmtpSecurity = SecureSocket.SslOnConnect,
            Credential = CredentialStyle.AppPassword,
            CredentialUrl = "https://service.mail.qq.com/",
            Advice = "QQ Mail uses an authorization code, not your QQ password. Enable IMAP/SMTP in "
                + "settings first, then generate the code.",
        },
        ["foxmail.com"] = new ProviderPreset
        {
            DisplayName = "Foxmail",
            ImapHost = "imap.qq.com",
            SmtpHost = "smtp.qq.com",
            SmtpPort = 465,
            SmtpSecurity = SecureSocket.SslOnConnect,
            Credential = CredentialStyle.AppPassword,
            Advice = "Foxmail addresses authenticate against QQ Mail with an authorization code.",
        },

        ["163.com"] = NetEase("NetEase 163", "163.com"),
        ["126.com"] = NetEase("NetEase 126", "126.com"),
        ["yeah.net"] = NetEase("NetEase yeah.net", "yeah.net"),

        ["protonmail.com"] = Proton(),
        ["proton.me"] = Proton(),
        ["pm.me"] = Proton(),
    };

    /// <summary>
    /// Resolves settings for an address. Returns a known preset, otherwise a guess built from the
    /// domain (<c>imap.example.com</c> / <c>smtp.example.com</c>), which is the convention most
    /// self-hosted and business mail follows. Never returns null; never touches the network.
    /// </summary>
    public static ProviderPreset ForEmail(string email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (email is null || at <= 0 || at == email.Length - 1)
            throw new ArgumentException("An email address is required to resolve provider settings.", nameof(email));

        return ForDomain(email[(at + 1)..]);
    }

    public static ProviderPreset ForDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var key = domain.Trim().Trim('.').ToLowerInvariant();

        if (ByDomain.TryGetValue(key, out var preset)) return preset;

        // A subdomain of a known provider (mail.example.co.uk) usually shares its settings.
        var dot = key.IndexOf('.');
        while (dot > 0 && dot < key.Length - 1)
        {
            var parent = key[(dot + 1)..];
            if (ByDomain.TryGetValue(parent, out var inherited)) return inherited;
            dot = key.IndexOf('.', dot + 1);
        }

        return new ProviderPreset
        {
            DisplayName = key,
            ImapHost = "imap." + key,
            SmtpHost = "smtp." + key,
            IsGuess = true,
            Advice = "This provider is not in the built-in list, so the hosts below are a guess from "
                + "the domain name. Check them against your provider's IMAP documentation.",
        };
    }

    /// <summary>Every domain with a hand-written preset, for help text and tests.</summary>
    public static IReadOnlyCollection<string> KnownDomains() => ByDomain.Keys.ToArray();

    private static ProviderPreset Gmail(string name) => new()
    {
        DisplayName = name,
        ImapHost = "imap.gmail.com",
        SmtpHost = "smtp.gmail.com",
        Credential = CredentialStyle.AppPassword,
        CredentialUrl = "https://myaccount.google.com/apppasswords",
        Advice = GmailAdvice,
    };

    private static ProviderPreset Microsoft(string name) => new()
    {
        DisplayName = name,
        ImapHost = "outlook.office365.com",
        SmtpHost = "smtp-mail.outlook.com",
        Credential = CredentialStyle.OAuthOnly,
        Advice = MicrosoftAdvice,
        Discouraged = MicrosoftAdvice,
    };

    private static ProviderPreset Yahoo(string name) => new()
    {
        DisplayName = name,
        ImapHost = "imap.mail.yahoo.com",
        SmtpHost = "smtp.mail.yahoo.com",
        SmtpPort = 465,
        SmtpSecurity = SecureSocket.SslOnConnect,
        Credential = CredentialStyle.AppPassword,
        CredentialUrl = "https://login.yahoo.com/account/security",
        Advice = "Yahoo requires an app password; your Yahoo password will be refused.",
    };

    private static ProviderPreset Icloud() => new()
    {
        DisplayName = "iCloud Mail",
        ImapHost = "imap.mail.me.com",
        SmtpHost = "smtp.mail.me.com",
        Credential = CredentialStyle.AppPassword,
        CredentialUrl = "https://account.apple.com/account/manage",
        Advice = "iCloud requires an app-specific password. Sign in with your full @icloud.com "
            + "address even if your Apple ID is a different address.",
    };

    private static ProviderPreset Fastmail() => new()
    {
        DisplayName = "Fastmail",
        ImapHost = "imap.fastmail.com",
        SmtpHost = "smtp.fastmail.com",
        SmtpPort = 465,
        SmtpSecurity = SecureSocket.SslOnConnect,
        Credential = CredentialStyle.AppPassword,
        CredentialUrl = "https://app.fastmail.com/settings/security/apppasswords",
        Advice = "Fastmail requires an app password scoped to IMAP and SMTP.",
    };

    private static ProviderPreset Gmx(string name) => new()
    {
        DisplayName = name,
        ImapHost = "imap.gmx.com",
        SmtpHost = "mail.gmx.com",
        Advice = "IMAP must be enabled in the GMX web interface before it will accept a login.",
    };

    private static ProviderPreset Yandex() => new()
    {
        DisplayName = "Yandex Mail",
        ImapHost = "imap.yandex.com",
        SmtpHost = "smtp.yandex.com",
        SmtpPort = 465,
        SmtpSecurity = SecureSocket.SslOnConnect,
        Credential = CredentialStyle.AppPassword,
        Advice = "Yandex requires an app password created in account security settings.",
    };

    private static ProviderPreset NetEase(string name, string domain) => new()
    {
        DisplayName = name,
        ImapHost = "imap." + domain,
        SmtpHost = "smtp." + domain,
        SmtpPort = 465,
        SmtpSecurity = SecureSocket.SslOnConnect,
        Credential = CredentialStyle.AppPassword,
        Advice = "NetEase uses an authorization code, not your mailbox password. Enable IMAP/SMTP "
            + "in the web settings, then generate the code.",
    };

    private static ProviderPreset Proton() => new()
    {
        DisplayName = "Proton Mail (via Proton Bridge)",
        ImapHost = "127.0.0.1",
        ImapPort = 1143,
        ImapSecurity = SecureSocket.StartTls,
        SmtpHost = "127.0.0.1",
        SmtpPort = 1025,
        SmtpSecurity = SecureSocket.StartTls,
        Credential = CredentialStyle.AppPassword,
        Advice = "Proton has no public IMAP. Install Proton Bridge, keep it running, and use the "
            + "username and password Bridge shows you — not your Proton account password.",
    };
}
