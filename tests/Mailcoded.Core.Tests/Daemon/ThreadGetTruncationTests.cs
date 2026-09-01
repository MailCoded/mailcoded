using System.Globalization;
using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Daemon;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

/// <summary>AGENT-INTERFACE §13.3: a client that reads <c>truncated: false</c> must have the whole
/// conversation, so the flag has to be read against the limit that was actually applied.</summary>
public sealed class ThreadGetTruncationTests
{
    private const int ThreadSize = 505;

    [Fact]
    public async Task WithNoLimitTheCoreDefaultTruncationIsReported()
    {
        await using var fixture = await ThreadFixture.CreateAsync(ThreadSize, TestContext.Current.CancellationToken);
        var result = await fixture.ThreadGetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(500, result.Count);
        Assert.True(
            result.Truncated,
            "thread.get returned 500 of 505 messages; reporting truncated:false tells the client it has "
            + "the whole conversation.");
    }

    [Theory]
    [InlineData(25, 25, true)]
    [InlineData(505, 505, true)]
    [InlineData(1000, ThreadSize, false)]
    [InlineData(5000, ThreadSize, false)]
    public async Task AnExplicitLimitIsReportedAgainstWhatWasApplied(int limit, int expected, bool truncated)
    {
        await using var fixture = await ThreadFixture.CreateAsync(ThreadSize, TestContext.Current.CancellationToken);
        var result = await fixture.ThreadGetAsync(limit, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Count);
        Assert.Equal(truncated, result.Truncated);
    }

    private sealed record ThreadPage(int Count, bool Truncated);

    private sealed class ThreadFixture : IAsyncDisposable
    {
        private readonly SurfaceWorkspace _workspace;
        private readonly DaemonHost _host;
        private readonly RpcDispatcher _dispatcher;
        private readonly string _threadKey;

        private ThreadFixture(SurfaceWorkspace workspace, DaemonHost host, RpcDispatcher dispatcher, string threadKey)
        {
            _workspace = workspace;
            _host = host;
            _dispatcher = dispatcher;
            _threadKey = threadKey;
        }

        public static async Task<ThreadFixture> CreateAsync(int size, CancellationToken ct)
        {
            var workspace = new SurfaceWorkspace("thread-truncation");
            var log = new StderrLog(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);
            var host = DaemonHost.Create(workspace.DatabasePath, log, isPrimary: true);

            try
            {
                var threadKey = await SeedAsync(host, size, ct).ConfigureAwait(false);
                return new ThreadFixture(workspace, host, new RpcDispatcher(host, log), threadKey);
            }
            catch (Exception)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                workspace.Dispose();
                throw;
            }
        }

        public async Task<ThreadPage> ThreadGetAsync(int? limit, CancellationToken ct)
        {
            var json = limit is { } value
                ? $"{{\"threadKey\":\"{_threadKey}\",\"limit\":{value.ToString(CultureInfo.InvariantCulture)}}}"
                : $"{{\"threadKey\":\"{_threadKey}\"}}";

            using var parameters = JsonDocument.Parse(json);
            var payload = await _dispatcher
                .DispatchAsync(RpcMethods.ThreadGet, parameters.RootElement, ct)
                .ConfigureAwait(false);

            using var result = JsonDocument.Parse(payload);
            return new ThreadPage(
                result.RootElement.GetProperty("messages").GetArrayLength(),
                result.RootElement.GetProperty("truncated").GetBoolean());
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync().ConfigureAwait(false);
            _workspace.Dispose();
        }

        private static async Task<string> SeedAsync(DaemonHost host, int size, CancellationToken ct)
        {
            var accountId = await StoreSeeder.AddAccountAsync(host.Store, ct).ConfigureAwait(false);
            var folderId = await StoreSeeder.AddInboxAsync(host.Store, accountId, ct).ConfigureAwait(false);
            var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var envelopes = new List<RemoteEnvelope>(size);
            for (var i = 0; i < size; i++)
            {
                envelopes.Add(new RemoteEnvelope
                {
                    Uid = new Uid((uint)(i + 1)),
                    Flags = MessageFlags.Unread,
                    MessageIdHeader = "thread-" + i.ToString(CultureInfo.InvariantCulture) + "@example.test",
                    References = ["thread-root@example.test"],
                    Subject = "A very long conversation",
                    From = "Ada <ada@example.test>",
                    To = "golden@example.test",
                    DateUtc = start.AddMinutes(i),
                    Size = 100,
                });
            }

            await host.Store
                .IngestEnvelopesAsync(folderId, envelopes, ReferencesThreader.Instance, ct)
                .ConfigureAwait(false);

            await host.Store.RecountFolderAsync(folderId, ct).ConfigureAwait(false);

            var id = host.Store.FindMessage(folderId, new Uid(1), ct)
                ?? throw new InvalidOperationException("The seeded thread was not stored.");

            var envelope = host.Store.GetEnvelope(id, ct)
                ?? throw new InvalidOperationException("The seeded envelope was not stored.");

            return (envelope.ThreadKey?.Value)
                ?? throw new InvalidOperationException("The seeded envelope has no thread key.");
        }
    }
}
