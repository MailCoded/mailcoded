using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Embedding;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class VectorBackfillTests : IDisposable
{
    private readonly string _modelDirectory = Directory.CreateTempSubdirectory("mailcoded-backfill-").FullName;

    public void Dispose() => Directory.Delete(_modelDirectory, recursive: true);

    [Fact]
    public async Task EmbedsEveryMessageThatHasABody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks over the meeting room", ct);
        await AddAsync(temp, folder, 2, "Quarterly report", "the budget for the garden", ct);

        var backfill = Backfill(temp);
        var report = await backfill.RunBatchAsync(ct);

        Assert.Equal(2, report.Embedded);
        Assert.Equal(0, report.Remaining);

        var modelId = await backfill.PrepareAsync(ct);
        Assert.Equal(2, temp.Store.ReadVectors(modelId, TinyModel.Dimensions, ct).Count);
    }

    /// <summary>An envelope with no body yet is not work: there is nothing to embed.</summary>
    [Fact]
    public async Task LeavesAMessageAloneUntilItsBodyArrives()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        await temp.Store.IngestEnvelopesAsync(
            folder,
            [StoreSeed.Envelope(1, subject: "Roof repair", date: StoreSeed.BaseDate, flags: MessageFlags.Unread)],
            null,
            ct);

        var backfill = Backfill(temp);
        Assert.Equal(0, (await backfill.RunBatchAsync(ct)).Embedded);

        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the seeded message");
        await temp.Store.SetBodyTextAsync(id, "the roof leaks", null, false, ct);

        Assert.Equal(1, (await backfill.RunBatchAsync(ct)).Embedded);
    }

    [Fact]
    public async Task DoesNotEmbedTheSameMessageTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);

        var backfill = Backfill(temp);
        Assert.Equal(1, (await backfill.RunBatchAsync(ct)).Embedded);
        Assert.Equal(0, (await backfill.RunBatchAsync(ct)).Embedded);
    }

    /// <summary>The queue is the absence of a row, so a second process picks up exactly where the
    /// first stopped without any state of its own.</summary>
    [Fact]
    public async Task ResumesWhereAPreviousRunStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        for (var uid = 1u; uid <= 5; uid++) await AddAsync(temp, folder, uid, $"Message {uid}", "the roof leaks", ct);

        var first = Backfill(temp, batchSize: 2);
        Assert.Equal(2, (await first.RunBatchAsync(ct)).Embedded);
        Assert.Equal(3, await first.RemainingAsync(ct));

        var second = Backfill(temp, batchSize: 2);
        Assert.Equal(2, (await second.RunBatchAsync(ct)).Embedded);
        Assert.Equal(1, (await second.RunBatchAsync(ct)).Embedded);
        Assert.Equal(0, await second.RemainingAsync(ct));
    }

    [Fact]
    public async Task EmbedsARewrittenBodyAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);

        var backfill = Backfill(temp);
        await backfill.RunBatchAsync(ct);

        var modelId = await backfill.PrepareAsync(ct);
        var before = Assert.Single(temp.Store.ReadVectors(modelId, TinyModel.Dimensions, ct));

        await temp.Store.SetBodyTextAsync(id, "the budget meeting notes for the garden", null, false, ct);
        Assert.Equal(1, (await backfill.RunBatchAsync(ct)).Embedded);

        var after = Assert.Single(temp.Store.ReadVectors(modelId, TinyModel.Dimensions, ct));
        Assert.NotEqual(before.Vector, after.Vector);
    }

    /// <summary>The bulk window drops indexes and runs synchronous=OFF. A second writer competing for
    /// the same thread would only stretch it, so the backfill waits instead.</summary>
    [Fact]
    public async Task StandsDownWhileABulkIngestWindowIsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);

        var backfill = Backfill(temp);
        await using (await temp.Store.BeginBulkIngestAsync(null, ct))
        {
            var duringWindow = await backfill.RunBatchAsync(ct);

            Assert.Equal(0, duringWindow.Embedded);
            Assert.Equal(0, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM vec_model", ct));
        }

        Assert.Equal(1, (await backfill.RunBatchAsync(ct)).Embedded);
    }

    /// <summary>Switching models must rebuild rather than reinterpret: comparing vectors made by two
    /// models is silently meaningless.</summary>
    [Fact]
    public async Task ANewModelRebuildsEveryVector()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);

        var original = Backfill(temp);
        await original.RunBatchAsync(ct);
        var originalId = await original.PrepareAsync(ct);

        var replacement = Backfill(temp, seed: 99);
        Assert.Equal(1, await replacement.RemainingAsync(ct));
        Assert.Equal(1, (await replacement.RunBatchAsync(ct)).Embedded);

        var replacementId = await replacement.PrepareAsync(ct);
        Assert.NotEqual(originalId, replacementId);
        Assert.Empty(temp.Store.ReadVectors(originalId, TinyModel.Dimensions, ct));
        Assert.Single(temp.Store.ReadVectors(replacementId, TinyModel.Dimensions, ct));
    }

    [Fact]
    public async Task StoresAVectorThatStillCarriesTheMeasuredSimilarity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);

        var backfill = Backfill(temp);
        await backfill.RunBatchAsync(ct);

        var modelId = await backfill.PrepareAsync(ct);
        var stored = Assert.Single(temp.Store.ReadVectors(modelId, TinyModel.Dimensions, ct));

        var embedder = TextEmbedder.Load(_modelDirectory);
        var expected = new sbyte[embedder.Dimensions];
        var expectedScale = embedder.Embed(TextEmbedder.Compose("Roof repair", "the roof leaks"), expected);

        var actual = new sbyte[embedder.Dimensions];
        Quantizer.FromBytes(stored.Vector, actual);

        Assert.True(
            Quantizer.Similarity(expected, expectedScale, actual, stored.Scale) > 0.99f,
            "the stored vector is not the one the embedder produces");
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var backfill = Backfill(temp, idleDelay: TimeSpan.FromMilliseconds(10));

        var running = backfill.RunAsync(null, cancellation.Token);
        await cancellation.CancelAsync();

        await running;
        Assert.True(running.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReportsProgressAsItGoes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        for (var uid = 1u; uid <= 4; uid++) await AddAsync(temp, folder, uid, $"Message {uid}", "the roof leaks", ct);

        var reports = new List<VectorBackfillReport>();
        var backfill = Backfill(temp, batchSize: 2);

        reports.Add(await backfill.RunBatchAsync(ct));
        reports.Add(await backfill.RunBatchAsync(ct));

        Assert.Equal([2, 2], reports.Select(r => r.Embedded));
        Assert.Equal([2L, 0L], reports.Select(r => r.Remaining));
    }

    private VectorBackfill Backfill(TempStore temp, int batchSize = 256, int seed = 17, TimeSpan? idleDelay = null)
    {
        TinyModel.Write(_modelDirectory, seed);
        return new VectorBackfill(
            temp.Store,
            TextEmbedder.Load(_modelDirectory),
            new VectorBackfillOptions
            {
                BatchSize = batchSize,
                IdleDelay = idleDelay ?? TimeSpan.FromSeconds(30),
                BusyDelay = TimeSpan.FromMilliseconds(10),
            });
    }

    private static async Task<FolderId> SeedAsync(TempStore temp, CancellationToken ct)
    {
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        return await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
    }

    private static async Task<LocalMessageId> AddAsync(
        TempStore temp,
        FolderId folder,
        uint uid,
        string subject,
        string body,
        CancellationToken ct)
    {
        await temp.Store.IngestEnvelopesAsync(
            folder,
            [StoreSeed.Envelope(uid, subject: subject, date: StoreSeed.BaseDate.AddMinutes(uid), flags: MessageFlags.Unread)],
            null,
            ct);

        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(uid), ct), "the seeded message");
        await temp.Store.SetBodyTextAsync(id, body, null, false, ct);
        return id;
    }
}
