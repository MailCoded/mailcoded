using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Providers;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>A command issued while a sync still holds the sync connection runs on a second connection instead of waiting for the sync to end.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.InteractiveLane)]
public sealed class InteractiveLaneTests : IClassFixture<MailStackFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Blink = TimeSpan.FromSeconds(3);

    private readonly MailStackFixture _stack;

    public InteractiveLaneTests(MailStackFixture stack) => _stack = stack;

    private async Task<ImapProvider> ConnectAsync(bool lane, CancellationToken ct)
    {
        var provider = new ImapProvider(
            SystemClock.Instance,
            TestTransportOptions.Fast with { InteractiveLane = lane });
        try
        {
            await provider.ConnectAsync(_stack.BuildAccountConfig(MailStackFixture.ExternalSecretRef), _stack.ExternalSecrets, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return provider;
    }

    private async Task<(FolderRef Folder, Uid Target, string Subject, IAsyncEnumerator<SyncEvent> Sync)> HoldSyncAsync(
        ImapProvider provider,
        CancellationToken ct)
    {
        var subject = "lane target " + Guid.NewGuid().ToString("N");
        var corpus = new List<byte[]>(SeedMail.Corpus(_stack.Credentials.Mailbox, 60, "lanefiller"))
        {
            SeedMail.Build(_stack.Credentials.Mailbox, subject, "Body fetched while the sync holds the connection.", 999),
        };

        var uids = await MailStackFixture.AppendWithAsync(provider, FolderPath.Inbox, corpus, ct);
        var folder = new FolderRef(FolderId.None, FolderPath.Create(FolderPath.Inbox));
        await provider.OpenFolderAsync(folder, false, ct);

        var sync = provider.SyncFolderAsync(folder, SyncPlan.Full([]), ct).GetAsyncEnumerator(ct);
        Assert.True(await sync.MoveNextAsync(), "The sync produced no event, so it never held the connection.");

        return (folder, uids[^1], subject, sync);
    }

    [Fact]
    public async Task A_body_fetch_and_a_flag_push_complete_while_a_sync_is_paused_mid_stream()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        await using var provider = await ConnectAsync(lane: true, ct);
        var (folder, target, subject, sync) = await HoldSyncAsync(provider, ct);
        await using var _ = sync;

        using var body = new MemoryStream();
        await provider.FetchRawMessageToAsync(folder, target, body, ct).WaitAsync(Patience, ct);
        Assert.Contains(subject, System.Text.Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);

        await provider.SetFlagsAsync(folder, target, new FlagDelta { Add = MessageFlags.Flagged }, ct).WaitAsync(Patience, ct);

        var remote = Assert.Single(
            await _stack.ReadServerFolderAsync(FolderPath.Inbox, ct),
            e => string.Equals(e.Subject, subject, StringComparison.Ordinal));
        Assert.True(remote.Flags.HasFlag(MessageFlags.Flagged), "The \\Flagged flag pushed on the second connection never reached the server.");
    }

    [Fact]
    public async Task Without_the_lane_the_same_fetch_waits_until_the_sync_lets_go()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        await using var provider = await ConnectAsync(lane: false, ct);
        var (folder, target, _, sync) = await HoldSyncAsync(provider, ct);

        using var body = new MemoryStream();
        var fetch = provider.FetchRawMessageToAsync(folder, target, body, ct);

        Assert.NotSame(fetch, await Task.WhenAny(fetch, Task.Delay(Blink, ct)));

        await sync.DisposeAsync();
        await fetch.WaitAsync(Patience, ct);
        Assert.True(body.Length > 0);
    }

    [Fact]
    public async Task An_idle_provider_with_the_lane_enabled_still_serves_a_fetch()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var provider = await ConnectAsync(lane: true, ct);
        var subject = "lane idle " + Guid.NewGuid().ToString("N");
        var uids = await MailStackFixture.AppendWithAsync(
            provider,
            FolderPath.Inbox,
            [SeedMail.Build(_stack.Credentials.Mailbox, subject, "Fetched on the only connection.", 1)],
            ct);

        using var body = new MemoryStream();
        await provider.FetchRawMessageToAsync(new FolderRef(FolderId.None, FolderPath.Create(FolderPath.Inbox)), uids[0], body, ct);

        Assert.Contains(subject, System.Text.Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }
}
