using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Tests.Surface;

/// <summary>A throwaway data directory. Every surface test gets its own store and blob tree.</summary>
internal sealed class SurfaceWorkspace : IDisposable
{
    public SurfaceWorkspace(string label)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "mailcoded-surface-tests",
            label + "-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, "store.db");

    public string PathTo(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Wall clock and monotonic ticks move independently, by hand.</summary>
internal sealed class ManualClock : IClock
{
    public ManualClock(DateTimeOffset? startUtc = null, long startTicks = 1_000_000)
    {
        UtcNow = startUtc ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Ticks = startTicks;
    }

    public DateTimeOffset UtcNow { get; set; }

    public long Ticks { get; set; }

    public void Advance(TimeSpan span)
    {
        Ticks += (long)span.TotalMilliseconds;
        UtcNow = UtcNow.Add(span);
    }

    /// <summary>Moves only the monotonic reading, which is the only legal basis for an interval.</summary>
    public void AdvanceMonotonic(TimeSpan span) => Ticks += (long)span.TotalMilliseconds;

    /// <summary>Moves only the wall clock, as an NTP step or a suspend/resume would.</summary>
    public void StepWallClock(TimeSpan span) => UtcNow = UtcNow.Add(span);
}

internal readonly record struct RecordedSend(EmailAddress From, IReadOnlyList<EmailAddress> Recipients, long SizeBytes);

/// <summary>An SMTP sink. Nothing leaves the process; the raw bytes are kept only to assert on them.</summary>
internal sealed class RecordingMailSender : IMailSender
{
    private readonly List<RecordedSend> _sends = [];

    public IReadOnlyList<RecordedSend> Sends => _sends;

    public string Response { get; set; } = "250 2.0.0 Ok: queued as ABCDEF";

    public Exception? Failure { get; set; }

    public long? MaxMessageSize => null;

    public bool SupportsSmtpUtf8 => true;

    public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => Task.CompletedTask;

    public Task<string> SendAsync(
        byte[] raw,
        EmailAddress from,
        IReadOnlyList<EmailAddress> recipients,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(recipients);

        if (Failure is { } failure) return Task.FromException<string>(failure);

        _sends.Add(new RecordedSend(from, [.. recipients], raw.LongLength));
        return Task.FromResult(Response);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Scoped env vars; the static gate serializes tests over this process-wide state.</summary>
internal sealed class EnvironmentScope : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly List<(string Name, string? Previous)> _saved = [];
    private bool _released;

    private EnvironmentScope()
    {
    }

    public static EnvironmentScope Set(params (string Name, string? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        Gate.Wait();
        var scope = new EnvironmentScope();

        try
        {
            foreach (var (name, value) in values)
            {
                scope._saved.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }
        catch (Exception)
        {
            scope.Dispose();
            throw;
        }

        return scope;
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;

        for (var i = _saved.Count - 1; i >= 0; i--)
            Environment.SetEnvironmentVariable(_saved[i].Name, _saved[i].Previous);

        Gate.Release();
    }
}

/// <summary>Deterministic store fixtures: one account, one folder, hand-written messages.</summary>
internal static class StoreSeeder
{
    public const string AccountEmail = "golden@example.test";
    public const string SecretRef = "imap:golden@example.test";

    public static Task<AccountId> AddAccountAsync(
        SqliteStore store,
        CancellationToken ct,
        string email = AccountEmail,
        bool withSmtp = false)
    {
        ArgumentNullException.ThrowIfNull(store);

        return store.AddAccountAsync(
            new AccountConfig
            {
                Email = email,
                DisplayName = "Golden Tester",
                Provider = ProviderKind.Imap,
                Imap = new ImapConfig
                {
                    Host = "imap.example.test",
                    Port = 993,
                    Security = SecureSocket.SslOnConnect,
                    Username = "golden",
                },
                Smtp = withSmtp
                    ? new SmtpConfig { Host = "smtp.example.test", Port = 587, Security = SecureSocket.StartTls }
                    : null,
                Auth = AuthKind.Password,
                SecretRef = "imap:" + email,
            },
            ct);
    }

    public static async Task<FolderId> AddInboxAsync(SqliteStore store, AccountId accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var ids = await store.UpsertFoldersAsync(
            accountId,
            [new RemoteFolder { Path = FolderPath.Create(FolderPath.Inbox), Role = FolderRole.Inbox }],
            ct).ConfigureAwait(false);

        return ids[0];
    }

    /// <summary>Ingests one envelope and fills its body, so no provider is ever needed to read it.</summary>
    public static async Task<LocalMessageId> AddMessageAsync(
        SqliteStore store,
        FolderId folderId,
        uint uid,
        string messageId,
        string subject,
        string from,
        string to,
        DateTimeOffset dateUtc,
        string bodyText,
        long size,
        CancellationToken ct,
        byte[]? raw = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        BlobRef? blob = raw is null ? null : await store.StoreBlobAsync(raw, ct).ConfigureAwait(false);

        await store.IngestEnvelopesAsync(
            folderId,
            [
                new RemoteEnvelope
                {
                    Uid = new Uid(uid),
                    Flags = MessageFlags.Unread,
                    MessageIdHeader = messageId,
                    Subject = subject,
                    From = from,
                    To = to,
                    DateUtc = dateUtc,
                    Size = size,
                },
            ],
            Mailcoded.Core.Domain.Threading.ReferencesThreader.Instance,
            ct).ConfigureAwait(false);

        var id = store.FindMessage(folderId, new Uid(uid), ct)
            ?? throw new InvalidOperationException("The seeded message was not stored.");

        await store.SetBodyTextAsync(id, bodyText, blob?.Id, false, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>A multipart/alternative message whose HTML part must never reach an agent.</summary>
    public static byte[] HtmlAndTextMessage() =>
        System.Text.Encoding.ASCII.GetBytes(
            "From: Ada <ada@example.test>\r\n"
            + "To: golden@example.test\r\n"
            + "Subject: Quarterly invoice\r\n"
            + "Message-ID: <golden-html@example.test>\r\n"
            + "Date: Tue, 02 Jan 2024 03:04:05 +0000\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: multipart/alternative; boundary=\"bnd\"\r\n"
            + "\r\n"
            + "--bnd\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "\r\n"
            + "The invoice total is 42 dollars.\r\n"
            + "\r\n"
            + "--bnd\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + "\r\n"
            + "<html><body><p>The invoice total is <b>42</b> dollars.</p>"
            + "<img src=\"https://tracker.example.invalid/pixel.gif\"></body></html>\r\n"
            + "\r\n"
            + "--bnd--\r\n");

    public static ParsedMessage Parse(byte[] raw) =>
        MessageParser.Default.Parse(raw, new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));
}
