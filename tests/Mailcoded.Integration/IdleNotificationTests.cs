using System.Globalization;
using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Protocol;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>M2 acceptance: IDLE turns an external APPEND into notify.mail.added within 5 seconds.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.Idle)]
public sealed class IdleNotificationTests : IClassFixture<MailStackFixture>
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    private const string SecretRef = "imap:integration-daemon";

    private readonly MailStackFixture _stack;

    public IdleNotificationTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task An_external_append_produces_notify_mail_added_within_five_seconds()
    {
        _stack.SkipUnlessAvailable();

        var launch = DaemonLocator.TryLocate(out var reason);
        Assert.SkipWhen(launch is null, "Integration test SKIPPED: " + reason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("idle");
        await using var daemon = DaemonProcess.Start(launch!, workspace.DatabasePath, _stack.Credentials);

        await daemon.RequestAsync(
            RpcMethods.Initialize,
            writer =>
            {
                writer.WriteString("clientName", "mailcoded-integration");
                writer.WriteString("clientVersion", "0.1");
                writer.WriteNumber("protocolVersion", ProtocolConstants.Version);
                writer.WriteString("interface", "rpc");
            },
            CallTimeout,
            ct);

        await daemon.RequestAsync(
            RpcMethods.SecretSet,
            writer =>
            {
                writer.WriteString("ref", SecretRef);
                writer.WriteString("value", _stack.Credentials.ImapPassword);
            },
            CallTimeout,
            ct);

        var added = await daemon.RequestAsync(RpcMethods.AccountAdd, WriteAccount, CallTimeout, ct);
        var accountId = added.GetProperty("accountId").GetInt64();

        await daemon.RequestAsync(
            RpcMethods.Sync,
            writer => writer.WriteNumber("accountId", accountId),
            CallTimeout,
            ct);

        var folders = await daemon.RequestAsync(
            RpcMethods.FolderList,
            writer => writer.WriteNumber("accountId", accountId),
            CallTimeout,
            ct);

        var inboxId = FindInbox(folders);

        await daemon.RequestAsync(
            RpcMethods.WatchSubscribe,
            writer =>
            {
                writer.WriteNumber("accountId", accountId);
                writer.WriteStartArray("folderIds");
                writer.WriteNumberValue(inboxId);
                writer.WriteEndArray();
            },
            CallTimeout,
            ct);

        // The IDLE connection has to be established before the APPEND, or the test measures setup.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        var subject = "idle probe " + Guid.NewGuid().ToString("N");
        await _stack.AppendAsync(
            FolderPath.Inbox,
            [SeedMail.Build(_stack.Credentials.Mailbox, subject, "Appended by a second IMAP client.", 900)],
            ct);

        var start = Environment.TickCount64;
        var notification = await daemon.WaitForNotificationAsync(RpcNotifications.MailAdded, Budget, ct);
        var elapsed = Environment.TickCount64 - start;

        Assert.True(
            notification is not null,
            $"No {RpcNotifications.MailAdded} arrived within {Budget.TotalSeconds:F0}s.{Environment.NewLine}{daemon.Diagnostics}");

        Assert.True(
            elapsed <= (long)Budget.TotalMilliseconds,
            $"{RpcNotifications.MailAdded} took {elapsed.ToString(CultureInfo.InvariantCulture)} ms.");

        var payload = notification!.Value;
        Assert.Equal(accountId, payload.GetProperty("accountId").GetInt64());
        Assert.Equal(inboxId, payload.GetProperty("folderId").GetInt64());
        Assert.True(payload.GetProperty("count").GetInt32() >= 1);
    }

    private void WriteAccount(Utf8JsonWriter writer)
    {
        writer.WriteString("email", _stack.Credentials.Mailbox);
        writer.WriteString("displayName", "mailcoded integration");
        writer.WriteString("provider", ProviderKinds.Imap);
        writer.WriteString("secretRef", SecretRef);

        writer.WriteStartObject("imap");
        writer.WriteString("host", _stack.Imap.Host);
        writer.WriteNumber("port", _stack.Imap.Port);
        writer.WriteString("security", SecurityModes.None);
        writer.WriteString("username", _stack.Credentials.Mailbox);
        writer.WriteStartArray("watchFolders");
        writer.WriteStringValue(FolderPath.Inbox);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartObject("auth");
        writer.WriteString("kind", AuthKinds.Password);
        writer.WriteEndObject();
    }

    private static long FindInbox(JsonElement folders)
    {
        foreach (var folder in folders.GetProperty("folders").EnumerateArray())
        {
            var name = folder.GetProperty("name").GetString();
            if (string.Equals(name, FolderPath.Inbox, StringComparison.OrdinalIgnoreCase))
                return folder.GetProperty("id").GetInt64();
        }

        throw new InvalidOperationException("The daemon reported no INBOX after the initial sync.");
    }
}
