using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>Two RPC requests share one per-account SmtpSender, and MailKit allows exactly one
/// command in flight per client. Overlapping submissions must queue, not interleave.</summary>
public sealed class SmtpCommandOrderTests
{
    private static readonly string[] EnvelopeVerbs = ["MAIL", "RCPT", "DATA"];

    [Fact]
    public async Task TwoConcurrentSubmissionsOnOneSenderDoNotInterleave()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = FakeSmtpServer.Start(advertiseAuth: false);

        await using var sender = new SmtpSender(new TestClock(), Options);
        await sender.ConnectAsync(Account(server.Port), new RecordingSecretStore(), ct);

        var from = EmailAddress.Parse("alice@example.com");
        IReadOnlyList<EmailAddress> to = [EmailAddress.Parse("bob@example.org")];

        var responses = await Task.WhenAll(
            Task.Run(() => sender.SendAsync(Message("FIRST-SUBMISSION"), from, to, ct), ct),
            Task.Run(() => sender.SendAsync(Message("SECOND-SUBMISSION"), from, to, ct), ct));

        foreach (var response in responses)
            Assert.False(string.IsNullOrEmpty(response), "a submission that was accepted has a reply");

        var envelope = server.Commands
            .Where(command => EnvelopeVerbs.Contains(Verb(command), StringComparer.Ordinal))
            .Select(Verb)
            .ToArray();

        Assert.Equal(new[] { "MAIL", "RCPT", "DATA", "MAIL", "RCPT", "DATA" }, envelope);

        Assert.Equal(2, server.Messages.Count);
        Assert.Contains(server.Messages, m => m.Contains("FIRST-SUBMISSION", StringComparison.Ordinal));
        Assert.Contains(server.Messages, m => m.Contains("SECOND-SUBMISSION", StringComparison.Ordinal));

        foreach (var message in server.Messages)
        {
            // A command that landed inside DATA is a desynchronized session, not a delivery.
            Assert.DoesNotContain("MAIL FROM:", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RCPT TO:", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Verb(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
            ? first.ToUpperInvariant()
            : string.Empty;

    /// <summary>Bodies large enough that DATA spans several socket writes, so an unqueued second
    /// submission would land in the middle of the first one.</summary>
    private static byte[] Message(string marker) => Encoding.ASCII.GetBytes(
        "From: alice@example.com\r\n"
        + "To: bob@example.org\r\n"
        + $"Subject: {marker}\r\n"
        + $"Message-Id: <{marker.ToLowerInvariant()}@example.com>\r\n"
        + "Date: Thu, 15 Jan 2026 12:00:00 +0000\r\n"
        + "MIME-Version: 1.0\r\n"
        + "Content-Type: text/plain; charset=utf-8\r\n"
        + "\r\n"
        + string.Concat(Enumerable.Repeat(marker + " padding line to make DATA span many writes\r\n", 2_000)));

    private static MailTransportOptions Options => new()
    {
        ConnectTimeoutMs = 15_000,
        CommandTimeoutMs = 15_000,
    };

    private static AccountConfig Account(int port) => new()
    {
        Email = "relay@example.com",
        Provider = ProviderKind.Imap,
        Imap = new ImapConfig { Host = "127.0.0.1", Port = 143, Security = SecureSocket.None },
        Smtp = new SmtpConfig { Host = "127.0.0.1", Port = port, Security = SecureSocket.None },
        Auth = AuthKind.Password,
        SecretRef = "mailcoded:smtp:relay@example.com",
    };
}
