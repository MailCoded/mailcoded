using System.Net;
using System.Net.Sockets;
using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>A localhost bridge or internal MTA never advertises AUTH; it must still be sendable.</summary>
public sealed class UnauthenticatedRelayTests
{
    private static readonly byte[] Raw = Encoding.ASCII.GetBytes(
        "From: alice@example.com\r\n"
        + "To: bob@example.org\r\n"
        + "Subject: Payroll\r\n"
        + "Message-Id: <relay-1@example.com>\r\n"
        + "Date: Thu, 15 Jan 2026 12:00:00 +0000\r\n"
        + "MIME-Version: 1.0\r\n"
        + "Content-Type: text/plain; charset=utf-8\r\n"
        + "\r\n"
        + "Numbers attached in the next one.\r\n");

    [Fact]
    public async Task ARelayThatDoesNotAdvertiseAuthConnectsAndSends()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = FakeSmtpServer.Start(advertiseAuth: false);
        var secrets = new RecordingSecretStore();

        await using var sender = new SmtpSender(new TestClock(), Options);
        await sender.ConnectAsync(Account(server.Port), secrets, ct);

        Assert.True(sender.IsConnected, "an unauthenticated relay is a usable submission path");

        var response = await sender.SendAsync(
            Raw,
            EmailAddress.Parse("alice@example.com"),
            [EmailAddress.Parse("bob@example.org")],
            ct);

        Assert.False(string.IsNullOrEmpty(response));
        Assert.False(secrets.WasRead, "no credential may be read from a server that does not offer AUTH");

        var delivered = Assert.Single(server.Messages);
        Assert.Contains("Subject: Payroll", delivered, StringComparison.Ordinal);
        Assert.Contains(
            server.Commands,
            command => command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)
                && command.Contains("bob@example.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServerThatAdvertisesAuthWithNoStoredCredentialStillFailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = FakeSmtpServer.Start(advertiseAuth: true);
        var secrets = new RecordingSecretStore();

        await using var sender = new SmtpSender(new TestClock(), Options);

        var failure = await Assert.ThrowsAsync<ProviderException>(
            () => sender.ConnectAsync(Account(server.Port), secrets, ct));

        Assert.Equal(FailureCategory.Auth, failure.Category);
        Assert.False(sender.IsConnected);
        Assert.True(secrets.WasRead);
    }

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

/// <summary>Reports that nothing is stored, and remembers whether anything asked.</summary>
internal sealed class RecordingSecretStore : ISecretStore
{
    public bool WasRead { get; private set; }

    public string BackendName => "test-null";

    public bool IsAvailable => true;

    public Task SetAsync(string secretRef, string value, CancellationToken ct) => Task.CompletedTask;

    public Task<string?> GetAsync(string secretRef, CancellationToken ct)
    {
        WasRead = true;
        return Task.FromResult<string?>(null);
    }

    public Task DeleteAsync(string secretRef, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A loopback SMTP sink. Nothing leaves the machine; every command is kept for assertions.</summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly List<string> _commands = [];
    private readonly List<string> _messages = [];
    private readonly bool _advertiseAuth;
    private readonly Task _loop;

    private FakeSmtpServer(TcpListener listener, bool advertiseAuth)
    {
        _listener = listener;
        _advertiseAuth = advertiseAuth;
        _loop = Task.Run(AcceptAsync);
    }

    public static FakeSmtpServer Start(bool advertiseAuth)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeSmtpServer(listener, advertiseAuth);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<string> Commands
    {
        get { lock (_gate) return [.. _commands]; }
    }

    public IReadOnlyList<string> Messages
    {
        get { lock (_gate) return [.. _messages]; }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();

        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // The listener is torn down mid-accept by design.
        }

        _cts.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            using (client)
            {
                try
                {
                    await ServeAsync(client);
                }
                catch (Exception)
                {
                    // A client that hangs up mid-session is not a server error.
                }
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

        await writer.WriteLineAsync("220 fake.mailcoded.test ESMTP");

        while (await reader.ReadLineAsync(_cts.Token) is { } line)
        {
            lock (_gate) _commands.Add(line);

            var verb = line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
                ? first.ToUpperInvariant()
                : string.Empty;

            switch (verb)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-fake.mailcoded.test");
                    await writer.WriteLineAsync("250-SIZE 10485760");
                    await writer.WriteLineAsync("250-8BITMIME");
                    if (_advertiseAuth) await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                    await writer.WriteLineAsync("250 HELP");
                    break;

                case "HELO":
                    await writer.WriteLineAsync("250 fake.mailcoded.test");
                    break;

                case "DATA":
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    await ReadMessageAsync(reader);
                    await writer.WriteLineAsync("250 2.0.0 Ok: queued as FAKE1");
                    break;

                case "AUTH":
                    await writer.WriteLineAsync("535 5.7.8 Authentication credentials invalid");
                    break;

                case "QUIT":
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    return;

                default:
                    await writer.WriteLineAsync("250 2.0.0 Ok");
                    break;
            }
        }
    }

    private async Task ReadMessageAsync(StreamReader reader)
    {
        var body = new StringBuilder();

        while (await reader.ReadLineAsync(_cts.Token) is { } line)
        {
            if (line == ".") break;
            body.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append('\n');
        }

        lock (_gate) _messages.Add(body.ToString());
    }
}
