using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Daemon;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class SyncProgressNotificationTests
{
    private static readonly StderrLog Log = new(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);

    [Fact]
    public async Task A_published_snapshot_is_a_framed_notify_sync_progress_with_camel_case_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new MemoryStream();
        await using var channel = new RpcChannel(new FrameWriter(sink), Log);
        var run = new SyncProgressPublisher(channel, new TestClock()).Begin();

        run.Report(Snapshot(SyncPhase.Envelopes, "INBOX", done: 12, total: 48));
        await channel.DrainAsync(TimeSpan.FromSeconds(5), ct);

        var frame = Assert.Single(Frames(sink.ToArray()));
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        Assert.Equal(RpcNotifications.SyncProgress, root.GetProperty("method").GetString());
        Assert.False(root.TryGetProperty("id", out _));

        var parameters = root.GetProperty("params");
        Assert.Equal(
            ["accountId", "atUtc", "done", "final", "folderId", "folderName", "phase", "total"],
            parameters.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, parameters.GetProperty("accountId").GetInt64());
        Assert.Equal(7, parameters.GetProperty("folderId").GetInt64());
        Assert.Equal("INBOX", parameters.GetProperty("folderName").GetString());
        Assert.Equal("envelopes", parameters.GetProperty("phase").GetString());
        Assert.Equal(12, parameters.GetProperty("done").GetInt32());
        Assert.Equal(48, parameters.GetProperty("total").GetInt32());
        Assert.False(parameters.GetProperty("final").GetBoolean());
    }

    [Fact]
    public async Task A_snapshot_without_a_total_leaves_the_member_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new MemoryStream();
        await using var channel = new RpcChannel(new FrameWriter(sink), Log);
        var run = new SyncProgressPublisher(channel, new TestClock()).Begin();

        run.Report(Snapshot(SyncPhase.Flags, "INBOX", done: 3, total: null));
        await channel.DrainAsync(TimeSpan.FromSeconds(5), ct);

        using var document = JsonDocument.Parse(Assert.Single(Frames(sink.ToArray())));
        var parameters = document.RootElement.GetProperty("params");
        Assert.False(parameters.TryGetProperty("total", out _));
        Assert.Equal("flags", parameters.GetProperty("phase").GetString());
    }

    [Fact]
    public void The_folder_walk_carries_no_folder()
    {
        var walk = new SyncProgress
        {
            AccountId = new AccountId(1),
            Phase = SyncPhase.Folders,
            Done = 2,
            Total = 5,
        };

        var wire = WireMapper.ToNotification(walk, TestClock.DefaultStart);

        Assert.Null(wire.FolderId);
        Assert.Null(wire.FolderName);
        Assert.Equal(SyncPhases.Folders, wire.Phase);
        var json = JsonSerializer.Serialize(wire, ProtocolJsonContext.Default.SyncProgressNotification);
        Assert.DoesNotContain("folderId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("folderName", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SyncPhase.Folders, "folders")]
    [InlineData(SyncPhase.Envelopes, "envelopes")]
    [InlineData(SyncPhase.Flags, "flags")]
    public void Every_phase_has_a_documented_wire_name(SyncPhase phase, string expected)
    {
        var wire = WireMapper.ToNotification(Snapshot(phase, "INBOX", 0, 0), TestClock.DefaultStart);

        Assert.Equal(expected, wire.Phase);
    }

    [Fact]
    public void An_undeclared_phase_is_refused_rather_than_sent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WireMapper.ToNotification(Snapshot((SyncPhase)99, "INBOX", 0, 0), TestClock.DefaultStart));
    }

    [Fact]
    public void The_notification_has_counts_and_identifiers_only()
    {
        var names = typeof(SyncProgressNotification)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            ["AccountId", "AtUtc", "Done", "Final", "FolderId", "FolderName", "Phase", "Total"],
            names);
    }

    [Fact]
    public async Task A_hostile_folder_name_is_escaped_on_the_wire()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new MemoryStream();
        await using var channel = new RpcChannel(new FrameWriter(sink), Log);
        var run = new SyncProgressPublisher(channel, new TestClock()).Begin();

        run.Report(Snapshot(SyncPhase.Envelopes, "\u001b[2J‮\r\nINBOX\"}", done: 1, total: 2));
        await channel.DrainAsync(TimeSpan.FromSeconds(5), ct);

        var raw = sink.ToArray();
        Assert.DoesNotContain((byte)0x1B, raw);
        using var document = JsonDocument.Parse(Assert.Single(Frames(raw)));
        Assert.Equal(
            "\u001b[2J‮\r\nINBOX\"}",
            document.RootElement.GetProperty("params").GetProperty("folderName").GetString());
    }

    [Fact]
    public async Task An_engine_sync_through_the_daemon_publisher_opens_and_closes_its_indicator()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new SurfaceWorkspace("sync-progress");
        var host = DaemonHost.Create(workspace.DatabasePath, null, Log, isPrimary: true);
        var sink = new MemoryStream();
        await using var channel = new RpcChannel(new FrameWriter(sink), Log);

        try
        {
            host.AttachWatch(channel);
            var account = await StoreSeeder.AddAccountAsync(host.Store, ct);
            var inbox = await StoreSeeder.AddInboxAsync(host.Store, account, ct);

            var provider = new ScriptedMailProvider();
            provider.Folder("INBOX", Info(total: 5), Arrivals());

            var run = host.Progress.Begin();
            try
            {
                await host.Sync.SyncFolderAsync(provider, inbox, run, ct);
            }
            finally
            {
                run.Complete();
            }

            await channel.DrainAsync(TimeSpan.FromSeconds(5), ct);

            var notes = Frames(sink.ToArray()).Select(f => JsonDocument.Parse(f)).ToList();
            try
            {
                Assert.NotEmpty(notes);
                Assert.All(notes, n => Assert.Equal(RpcNotifications.SyncProgress, n.RootElement.GetProperty("method").GetString()));

                var first = notes[0].RootElement.GetProperty("params");
                Assert.Equal("envelopes", first.GetProperty("phase").GetString());
                Assert.False(first.GetProperty("final").GetBoolean());

                var last = notes[^1].RootElement.GetProperty("params");
                Assert.True(last.GetProperty("final").GetBoolean());
                Assert.Equal(5, last.GetProperty("done").GetInt32());
                Assert.Equal(5, last.GetProperty("total").GetInt32());
                Assert.Equal(inbox.Value, last.GetProperty("folderId").GetInt64());
                Assert.Equal(1, notes.Count(n => n.RootElement.GetProperty("params").GetProperty("final").GetBoolean()));
                Assert.True(DateTimeOffset.TryParse(
                    last.GetProperty("atUtc").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            }
            finally
            {
                foreach (var note in notes) note.Dispose();
            }
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static SyncProgress Snapshot(SyncPhase phase, string folder, int done, int? total) => new()
    {
        AccountId = new AccountId(1),
        Phase = phase,
        FolderId = new FolderId(7),
        FolderPath = folder,
        Done = done,
        Total = total,
    };

    private static ServerFolderInfo Info(int total) => new()
    {
        Path = FolderPath.Create("INBOX"),
        UidValidity = new UidValidity(42),
        HighestModSeq = new ModSeq(100),
        UidNext = new Uid((uint)total + 1),
        TotalCount = total,
    };

    private static List<SyncEvent> Arrivals() =>
    [
        new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(1, modSeq: 100)),
        new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(2, modSeq: 100)),
        new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(3, modSeq: 100)),
        new SyncEvent.BatchComplete(new ModSeq(100), new Uid(3), 3),
        new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(4, modSeq: 100)),
        new SyncEvent.EnvelopeAdded(StoreSeed.Envelope(5, modSeq: 100)),
        new SyncEvent.BatchComplete(new ModSeq(100), new Uid(5), 2),
        new SyncEvent.BatchComplete(new ModSeq(100), new Uid(5), 0),
    ];

    private static List<string> Frames(byte[] raw)
    {
        var frames = new List<string>();
        var text = Encoding.UTF8.GetString(raw);
        var position = 0;

        while (position < text.Length)
        {
            var headerEnd = text.IndexOf("\r\n\r\n", position, StringComparison.Ordinal);
            Assert.True(headerEnd > 0, "A frame has no header terminator.");

            var header = text[position..headerEnd];
            Assert.StartsWith("Content-Length: ", header, StringComparison.Ordinal);
            var length = int.Parse(header["Content-Length: ".Length..], CultureInfo.InvariantCulture);

            var bodyStart = headerEnd + 4;
            var body = Encoding.UTF8.GetBytes(text[bodyStart..]).Take(length).ToArray();
            frames.Add(Encoding.UTF8.GetString(body));
            position = bodyStart + Encoding.UTF8.GetString(body).Length;
        }

        return frames;
    }
}
