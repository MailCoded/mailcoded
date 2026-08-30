using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;

namespace Mailcoded.Core.Tests.Send;

internal sealed record CapturedSubmission(
    EmailAddress From,
    IReadOnlyList<EmailAddress> Recipients,
    byte[] Raw);

/// <summary>An SMTP sink that keeps the exact DATA bytes and the RCPT TO list of every submission.</summary>
internal sealed class CapturingSender : IMailSender
{
    private readonly List<CapturedSubmission> _sends = [];

    public IReadOnlyList<CapturedSubmission> Sends => _sends;

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

        _sends.Add(new CapturedSubmission(from, [.. recipients], [.. raw]));
        return Task.FromResult("250 2.0.0 Ok: queued");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SendPathHarness : IDisposable
{
    private readonly TempStore _temp;
    private bool _disposed;

    private SendPathHarness(TempStore temp, SendService send, ConfirmTokenStore tokens, AccountId accountId)
    {
        _temp = temp;
        Send = send;
        Tokens = tokens;
        AccountId = accountId;
    }

    public static CallerContext Caller => CallerContext.For(CallerKind.Cli, "xunit");

    public SqliteStore Store => _temp.Store;

    public TestClock Clock => _temp.Clock;

    public SendService Send { get; }

    public ConfirmTokenStore Tokens { get; }

    public AccountId AccountId { get; }

    public CapturingSender Sender { get; } = new();

    public static async Task<SendPathHarness> CreateAsync(CancellationToken ct, params string[] approvedRecipients)
    {
        var temp = TempStore.Create();

        try
        {
            var policy = new AgentPolicy(
                new AgentPolicyOptions { SendEnabled = true, ApprovedRecipients = approvedRecipients },
                temp.Clock);

            var tokens = new ConfirmTokenStore(temp.Clock, store: temp.Store);
            var send = new SendService(
                temp.Store,
                MessageParser.Default,
                temp.Clock,
                new AuditLog(temp.Store, temp.Clock),
                policy,
                tokens);

            return new SendPathHarness(temp, send, tokens, await StoreSeed.AccountAsync(temp.Store, ct));
        }
        catch (Exception)
        {
            temp.Dispose();
            throw;
        }
    }

    public Task<SendPreview> PreviewAsync(DraftRequest draft, CancellationToken ct) =>
        Send.PreviewAsync(Caller, AccountId, draft, ct);

    public Task<SendResult> SendDraftAsync(long outboxId, string? token, CancellationToken ct) =>
        Send.SendAsync(Caller, Sender, null, outboxId, token, new SendOptions { AppendToSent = false }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _temp.Dispose();
    }
}
