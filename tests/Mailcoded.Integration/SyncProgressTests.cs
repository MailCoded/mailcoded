using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Integration.Support;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>notify.sync.progress through the shipped daemon, against a real server.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.Progress)]
public sealed class SyncProgressTests : IClassFixture<MailStackFixture>
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    private const string SecretRef = "imap:integration-daemon";
    private const int Seeded = 1100;

    private static readonly string[] DocumentedKeys =
        ["accountId", "folderId", "folderName", "phase", "done", "total", "final", "atUtc"];

    private readonly MailStackFixture _stack;

    public SyncProgressTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task A_folder_sync_that_asks_for_progress_reports_envelopes_and_closes_them_before_answering()
    {
        _stack.SkipUnlessAvailable();
        var launch = DaemonLocator.TryLocate(out var reason);
        Assert.SkipWhen(launch is null, "Integration test SKIPPED: " + reason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        using var workspace = TestWorkspace.Create("progress-folder");
        await using var daemon = DaemonProcess.Start(launch!, workspace.DatabasePath, _stack.Credentials);

        var (accountId, inboxId) = await OpenAccountAsync(daemon, ct);
        var before = await InboxTotalAsync(daemon, accountId, ct);
        daemon.DrainNotifications();

        await _stack.AppendAsync(FolderPath.Inbox, Mail(Seeded), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);

        await SyncAsync(daemon, accountId, inboxId, progress: true, ct);
        var events = Progress(daemon.DrainNotifications());

        Assert.All(events, e => Assert.Equal("envelopes", e.GetProperty("phase").GetString()));
        Assert.All(events, e => Assert.Equal(inboxId, e.GetProperty("folderId").GetInt64()));
        Assert.Contains(events, e => !e.GetProperty("final").GetBoolean());

        var finals = events.Where(e => e.GetProperty("final").GetBoolean()).ToList();
        var only = Assert.Single(finals);
        Assert.True(events[^1].GetProperty("final").GetBoolean(), "The final event is not the last one.");
        Assert.Equal(before + Seeded, only.GetProperty("done").GetInt32());
        Assert.Equal(before + Seeded, only.GetProperty("total").GetInt32());

        Assert.All(events, e => Assert.All(e.EnumerateObject(), p => Assert.Contains(p.Name, DocumentedKeys)));
    }

    [Fact]
    public async Task An_account_sync_that_asks_for_progress_walks_the_folders_and_closes_the_walk()
    {
        _stack.SkipUnlessAvailable();
        var launch = DaemonLocator.TryLocate(out var reason);
        Assert.SkipWhen(launch is null, "Integration test SKIPPED: " + reason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        using var workspace = TestWorkspace.Create("progress-account");
        await using var daemon = DaemonProcess.Start(launch!, workspace.DatabasePath, _stack.Credentials);

        var (accountId, _) = await OpenAccountAsync(daemon, ct);
        var folderCount = await FolderCountAsync(daemon, accountId, ct);
        daemon.DrainNotifications();

        await SyncAsync(daemon, accountId, null, progress: true, ct);
        var events = Progress(daemon.DrainNotifications());

        var first = events[0];
        Assert.Equal("folders", first.GetProperty("phase").GetString());
        Assert.False(first.TryGetProperty("folderId", out _));
        Assert.Equal(0, first.GetProperty("done").GetInt32());
        Assert.Equal(folderCount, first.GetProperty("total").GetInt32());

        var walkEnd = events.Last(e => e.GetProperty("phase").GetString() == "folders");
        Assert.True(walkEnd.GetProperty("final").GetBoolean());
        Assert.Equal(folderCount, walkEnd.GetProperty("done").GetInt32());
        Assert.Equal(folderCount, walkEnd.GetProperty("total").GetInt32());

        var opened = events.Where(e => !e.GetProperty("final").GetBoolean()).Select(Key).Distinct().ToList();
        foreach (var key in opened)
            Assert.Equal(1, events.Count(e => e.GetProperty("final").GetBoolean() && Key(e) == key));
    }

    [Fact]
    public async Task A_sync_that_does_not_ask_for_progress_gets_none()
    {
        _stack.SkipUnlessAvailable();
        var launch = DaemonLocator.TryLocate(out var reason);
        Assert.SkipWhen(launch is null, "Integration test SKIPPED: " + reason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        using var workspace = TestWorkspace.Create("progress-none");
        await using var daemon = DaemonProcess.Start(launch!, workspace.DatabasePath, _stack.Credentials);

        var (accountId, inboxId) = await OpenAccountAsync(daemon, ct);
        await _stack.AppendAsync(FolderPath.Inbox, Mail(5), ct);
        await SyncAsync(daemon, accountId, inboxId, progress: false, ct);

        Assert.Empty(Progress(daemon.DrainNotifications()));
    }

    [Fact]
    public async Task A_watched_folder_reports_progress_before_notify_mail_added()
    {
        _stack.SkipUnlessAvailable();
        var launch = DaemonLocator.TryLocate(out var reason);
        Assert.SkipWhen(launch is null, "Integration test SKIPPED: " + reason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        using var workspace = TestWorkspace.Create("progress-watch");
        await using var daemon = DaemonProcess.Start(launch!, workspace.DatabasePath, _stack.Credentials);

        var (accountId, inboxId) = await OpenAccountAsync(daemon, ct);

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

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        daemon.DrainNotifications();
        await _stack.AppendAsync(FolderPath.Inbox, Mail(1), ct);

        var seen = new List<(string Method, JsonElement Params)>();
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline && !seen.Any(n => n.Method == RpcNotifications.MailAdded))
        {
            await Task.Delay(100, ct);
            seen.AddRange(daemon.DrainNotifications());
        }

        var added = seen.FindIndex(n => n.Method == RpcNotifications.MailAdded);
        Assert.True(added >= 0, $"No {RpcNotifications.MailAdded} arrived.{Environment.NewLine}{daemon.Diagnostics}");

        var closing = seen.FindIndex(n =>
            n.Method == RpcNotifications.SyncProgress
            && n.Params.GetProperty("phase").GetString() == "envelopes"
            && n.Params.GetProperty("folderId").GetInt64() == inboxId
            && n.Params.GetProperty("final").GetBoolean());

        Assert.True(closing >= 0 && closing < added, "The envelopes indicator did not close before notify.mail.added.");
    }

    private static string Key(JsonElement e) =>
        e.GetProperty("phase").GetString() + "/" + (e.TryGetProperty("folderId", out var id) ? id.GetInt64() : 0);

    private static List<JsonElement> Progress(IReadOnlyList<(string Method, JsonElement Params)> notifications) =>
        [.. notifications.Where(n => n.Method == RpcNotifications.SyncProgress).Select(n => n.Params)];

    private IReadOnlyList<byte[]> Mail(int count)
    {
        var messages = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
            messages.Add(SeedMail.Build(_stack.Credentials.Mailbox, "progress probe " + i, "Seeded for the progress test.", i));
        return messages;
    }

    private static Task SyncAsync(DaemonProcess daemon, long accountId, long? folderId, bool progress, CancellationToken ct) =>
        daemon.RequestAsync(
            RpcMethods.Sync,
            writer =>
            {
                writer.WriteNumber("accountId", accountId);
                if (folderId is { } folder) writer.WriteNumber("folderId", folder);
                if (progress) writer.WriteBoolean("progress", true);
            },
            CallTimeout,
            ct);

    private static async Task<int> InboxTotalAsync(DaemonProcess daemon, long accountId, CancellationToken ct)
    {
        var folders = await daemon.RequestAsync(
            RpcMethods.FolderList,
            writer => writer.WriteNumber("accountId", accountId),
            CallTimeout,
            ct);

        foreach (var folder in folders.GetProperty("folders").EnumerateArray())
        {
            if (string.Equals(folder.GetProperty("name").GetString(), FolderPath.Inbox, StringComparison.OrdinalIgnoreCase))
                return folder.GetProperty("total").GetInt32();
        }

        throw new InvalidOperationException("The daemon reported no INBOX.");
    }

    private static async Task<int> FolderCountAsync(DaemonProcess daemon, long accountId, CancellationToken ct)
    {
        var folders = await daemon.RequestAsync(
            RpcMethods.FolderList,
            writer => writer.WriteNumber("accountId", accountId),
            CallTimeout,
            ct);

        return folders.GetProperty("folders").GetArrayLength();
    }

    private async Task<(long AccountId, long InboxId)> OpenAccountAsync(DaemonProcess daemon, CancellationToken ct)
    {
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

        await SyncAsync(daemon, accountId, null, progress: false, ct);
        var folders = await daemon.RequestAsync(
            RpcMethods.FolderList,
            writer => writer.WriteNumber("accountId", accountId),
            CallTimeout,
            ct);

        return (accountId, FindInbox(folders));
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
