using System.Security.Cryptography;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Integration.Support;

/// <summary>Container credentials taken from the environment and redacted everywhere they surface.</summary>
public sealed class TestCredentials
{
    public const string MailboxEnvVar = "MAILCODED_IT_MAILBOX";
    public const string ImapPasswordEnvVar = "MAILCODED_IT_IMAP_PASSWORD";
    public const string SmtpUserEnvVar = "MAILCODED_IT_SMTP_USER";
    public const string SmtpPasswordEnvVar = "MAILCODED_IT_SMTP_PASSWORD";

    public const string DefaultMailbox = "tester@mailcoded.test";

    private readonly string _imapPassword;
    private readonly string _smtpPassword;

    private TestCredentials(string mailbox, string imapPassword, string smtpUser, string smtpPassword)
    {
        Mailbox = mailbox;
        _imapPassword = imapPassword;
        SmtpUser = smtpUser;
        _smtpPassword = smtpPassword;
    }

    public static TestCredentials FromEnvironment() =>
        new(
            Read(MailboxEnvVar) ?? DefaultMailbox,
            Read(ImapPasswordEnvVar) ?? GenerateEphemeral(),
            Read(SmtpUserEnvVar) ?? "smtp-test",
            Read(SmtpPasswordEnvVar) ?? GenerateEphemeral());

    public string Mailbox { get; }

    public string SmtpUser { get; }

    public string ImapPassword => _imapPassword;

    public string SmtpPassword => _smtpPassword;

    /// <summary>Replaces every credential this run knows about before text reaches an assertion.</summary>
    public string Redact(string? text) =>
        SecretRedactor.RedactSubstrings(text, _imapPassword, _smtpPassword);

    public bool Leaks(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(_imapPassword, StringComparison.Ordinal) || text.Contains(_smtpPassword, StringComparison.Ordinal));

    public override string ToString() =>
        $"mailbox={Mailbox} smtpUser={SmtpUser} imapPassword={SecretRedactor.Mask} smtpPassword={SecretRedactor.Mask}";

    private static string GenerateEphemeral() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    private static string? Read(string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
