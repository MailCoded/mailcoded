using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Xunit;

namespace Mailcoded.Integration.Support;

/// <summary>Dovecot plus smtp4dev for one test class; with no runtime it starts nothing and
/// publishes <see cref="SkipReason"/> so every test skips loudly.</summary>
public sealed class MailStackFixture : IAsyncLifetime
{
    public const string ExternalSecretRef = "imap:integration-external";

    private DovecotServer? _imap;
    private Smtp4DevServer? _smtp;

    public TestCredentials Credentials { get; } = TestCredentials.FromEnvironment();

    public InMemorySecretStore ExternalSecrets { get; } = new();

    public string? SkipReason { get; private set; }

    public bool IsAvailable => SkipReason is null && _imap is not null && _smtp is not null;

    public DovecotServer Imap => _imap ?? throw new InvalidOperationException(SkipReason ?? "The IMAP server is not running.");

    public Smtp4DevServer Smtp => _smtp ?? throw new InvalidOperationException(SkipReason ?? "The SMTP sink is not running.");

    public async ValueTask InitializeAsync()
    {
        var reason = DockerProbe.UnavailableReason();
        if (reason is not null)
        {
            SkipReason = reason;
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            _imap = await DovecotServer.StartAsync(Credentials, cts.Token).ConfigureAwait(false);
            _smtp = await Smtp4DevServer.StartAsync(Credentials, cts.Token).ConfigureAwait(false);

            await ExternalSecrets.SetAsync(ExternalSecretRef, Credentials.ImapPassword, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The probe only finds an endpoint; the engine behind it can still be dead. Turn that
            // into a loud skip rather than five failures that look like product defects.
            SkipReason = "Integration tests were SKIPPED: the container runtime was found but would "
                + $"not start the mail stack ({ex.GetType().Name}: {Credentials.Redact(ex.Message)}). "
                + "The end-to-end path was NOT verified.";

            if (_smtp is not null) await _smtp.DisposeAsync().ConfigureAwait(false);
            if (_imap is not null) await _imap.DisposeAsync().ConfigureAwait(false);
            _smtp = null;
            _imap = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_smtp is not null) await _smtp.DisposeAsync().ConfigureAwait(false);
        if (_imap is not null) await _imap.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Fails the test rather than letting it pass without ever reaching a server.</summary>
    public void SkipUnlessAvailable() => Assert.SkipUnless(IsAvailable, SkipReason ?? "The mail stack did not start.");

    public AccountConfig BuildAccountConfig(string secretRef) => new()
    {
        Email = Credentials.Mailbox,
        DisplayName = "mailcoded integration",
        Provider = ProviderKind.Imap,
        Auth = AuthKind.Password,
        SecretRef = secretRef,
        Imap = new ImapConfig
        {
            Host = Imap.Host,
            Port = Imap.Port,
            Security = SecureSocket.None,
            Username = Credentials.Mailbox,
            WatchFolders = [FolderPath.Inbox],
        },
        Smtp = new SmtpConfig
        {
            Host = Smtp.Host,
            Port = Smtp.Port,
            Security = SecureSocket.None,
            Username = Credentials.SmtpUser,
        },
    };

    /// <summary>A second client on the same mailbox: what "externally" means in the IDLE and
    /// flag-landed-on-the-server assertions.</summary>
    public async Task<ImapProvider> ConnectExternalAsync(CancellationToken ct)
    {
        var provider = new ImapProvider(SystemClock.Instance, TestTransportOptions.Fast);
        try
        {
            await provider.ConnectAsync(BuildAccountConfig(ExternalSecretRef), ExternalSecrets, ct).ConfigureAwait(false);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return provider;
    }

    public async Task<IReadOnlyList<Uid>> AppendAsync(string folder, IReadOnlyList<byte[]> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);

        await using var provider = await ConnectExternalAsync(ct).ConfigureAwait(false);
        return await AppendWithAsync(provider, folder, messages, ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<Uid>> AppendWithAsync(
        IMailProvider provider,
        string folder,
        IReadOnlyList<byte[]> messages,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(messages);

        var target = new FolderRef(FolderId.None, FolderPath.Create(folder));
        var uids = new List<Uid>(messages.Count);

        foreach (var raw in messages)
        {
            var uid = await provider
                .AppendAsync(target, raw, MessageFlags.Unread, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
            if (uid is { } assigned) uids.Add(assigned);
        }

        return uids;
    }

    /// <summary>Reads the folder straight off the server, bypassing the local store entirely.</summary>
    public async Task<IReadOnlyList<RemoteEnvelope>> ReadServerFolderAsync(string folder, CancellationToken ct)
    {
        await using var provider = await ConnectExternalAsync(ct).ConfigureAwait(false);

        var target = new FolderRef(FolderId.None, FolderPath.Create(folder));
        await provider.OpenFolderAsync(target, false, ct).ConfigureAwait(false);

        var envelopes = new List<RemoteEnvelope>();
        await foreach (var change in provider.SyncFolderAsync(target, SyncPlan.Full([]), ct).ConfigureAwait(false))
        {
            if (change is SyncEvent.EnvelopeAdded added) envelopes.Add(added.Envelope);
        }

        return envelopes;
    }

    public async Task<UidValidity> ReadUidValidityAsync(string folder, CancellationToken ct)
    {
        await using var provider = await ConnectExternalAsync(ct).ConfigureAwait(false);
        var target = new FolderRef(FolderId.None, FolderPath.Create(folder));
        var info = await provider.OpenFolderAsync(target, false, ct).ConfigureAwait(false);
        return info.UidValidity;
    }
}

/// <summary>Container-local timings: IDLE has to surface a change well inside the 5 s budget.</summary>
public static class TestTransportOptions
{
    public static readonly MailTransportOptions Fast = MailTransportOptions.Default with
    {
        ConnectTimeoutMs = 20_000,
        CommandTimeoutMs = 60_000,
        IdleQuietMs = 150,
        IdleMaxCoalesceMs = 1_000,
        RandomSeed = 1,
    };
}

[CollectionDefinition("mail-stack", DisableParallelization = true)]
public sealed class MailStackCollection
{
    public const string Name = "mail-stack";
}
