using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>The vector table is derived data whose one correctness rule is that vectors from two
/// models never meet. These are the cases where they would.</summary>
public sealed class MessageVectorTests
{
    private static readonly VectorModel Model = new()
    {
        Fingerprint = "aaaa",
        Name = "tiny",
        Dimensions = 8,
        Pooling = "mean",
    };

    [Fact]
    public async Task RegisteringTheSameFingerprintTwiceGivesTheSameModel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var first = await temp.Store.EnsureVectorModelAsync(Model, ct);
        var again = await temp.Store.EnsureVectorModelAsync(Model with { Name = "renamed" }, ct);

        Assert.Equal(first, again);
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM vec_model", ct));
    }

    /// <summary>The fingerprint is the identity. Two different models wearing one would make every
    /// vector already stored under it incomparable with the ones about to be written.</summary>
    [Fact]
    public async Task RefusesAFingerprintThatAlreadyMeansADifferentModel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        await temp.Store.EnsureVectorModelAsync(Model, ct);

        await Assert.ThrowsAsync<StoreException>(
            () => temp.Store.EnsureVectorModelAsync(Model with { Dimensions = 384 }, ct));
        await Assert.ThrowsAsync<StoreException>(
            () => temp.Store.EnsureVectorModelAsync(Model with { Pooling = "cls" }, ct));
    }

    [Fact]
    public async Task AMessageWithABodyIsWorkUntilItHasAVector()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks over the meeting room", ct);
        Assert.Equal(1, temp.Store.CountPendingVectors(model, ct));
        Assert.Equal(id, Assert.Single(temp.Store.NextVectorWork(model, 10, ct)).MessageId);

        await temp.Store.SetMessageVectorsAsync(model, [new MessageVector(id, 0.5f, Bytes(8))], ct);

        Assert.Equal(0, temp.Store.CountPendingVectors(model, ct));
        Assert.Empty(temp.Store.NextVectorWork(model, 10, ct));
    }

    [Fact]
    public async Task WorkCarriesTheSubjectAndTheBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        var work = Assert.Single(temp.Store.NextVectorWork(model, 10, ct));

        Assert.Equal("Roof repair", work.Subject);
        Assert.Equal("the roof leaks", work.BodyText);
    }

    /// <summary>A rewritten body makes its vector a description of text that is no longer there.</summary>
    [Fact]
    public async Task RewritingTheBodyPutsTheMessageBackInTheQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await temp.Store.SetMessageVectorsAsync(model, [new MessageVector(id, 0.5f, Bytes(8))], ct);
        Assert.Equal(0, temp.Store.CountPendingVectors(model, ct));

        await temp.Store.SetBodyTextAsync(id, "the roof was repaired on tuesday", null, false, ct);

        Assert.Equal(1, temp.Store.CountPendingVectors(model, ct));
    }

    [Fact]
    public async Task RewritingABodyWithTheSameTextLeavesTheVectorAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await temp.Store.SetMessageVectorsAsync(model, [new MessageVector(id, 0.5f, Bytes(8))], ct);

        await temp.Store.SetBodyTextAsync(id, "the roof leaks", null, false, ct);

        Assert.Equal(0, temp.Store.CountPendingVectors(model, ct));
    }

    /// <summary>Switching models must not leave the old vectors where a scan can reach them.</summary>
    [Fact]
    public async Task VectorsFromAnotherModelAreNeitherReadNorCountedAsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);

        var old = await temp.Store.EnsureVectorModelAsync(Model, ct);
        var current = await temp.Store.EnsureVectorModelAsync(Model with { Fingerprint = "bbbb" }, ct);

        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await temp.Store.SetMessageVectorsAsync(old, [new MessageVector(id, 0.5f, Bytes(8))], ct);

        Assert.Empty(temp.Store.ReadVectors(current, 8, ct));
        Assert.Equal(1, temp.Store.CountPendingVectors(current, ct));
        Assert.Equal(id, Assert.Single(temp.Store.NextVectorWork(current, 10, ct)).MessageId);

        await temp.Store.SetMessageVectorsAsync(current, [new MessageVector(id, 0.25f, Bytes(8))], ct);

        Assert.Empty(temp.Store.ReadVectors(old, 8, ct));
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM msg_vec", ct));
    }

    [Fact]
    public async Task ReadsBackTheScaleAndTheBytesItWasGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await temp.Store.SetMessageVectorsAsync(model, [new MessageVector(id, 0.125f, Bytes(8))], ct);

        var stored = Assert.Single(temp.Store.ReadVectors(model, 8, ct));

        Assert.Equal(id, stored.MessageId);
        Assert.Equal(0.125f, stored.Scale);
        Assert.Equal(Bytes(8), stored.Vector);
    }

    /// <summary>One row of the wrong width must not take the whole search down with it.</summary>
    [Fact]
    public async Task SkipsAStoredVectorOfTheWrongWidth()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        var narrow = await AddAsync(temp, folder, 1, "One", "the roof leaks", ct);
        var right = await AddAsync(temp, folder, 2, "Two", "the budget meeting", ct);

        await temp.Store.SetMessageVectorsAsync(
            model,
            [new MessageVector(narrow, 1f, Bytes(4)), new MessageVector(right, 1f, Bytes(8))],
            ct);

        var stored = Assert.Single(temp.Store.ReadVectors(model, 8, ct));
        Assert.Equal(right, stored.MessageId);

        // Recorded, so the queue does not hand it back forever.
        Assert.Equal(0, temp.Store.CountPendingVectors(model, ct));
    }

    [Fact]
    public async Task ForgettingVectorsLeavesTheBodiesToRebuildFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var folder = await SeedAsync(temp, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await temp.Store.SetMessageVectorsAsync(model, [new MessageVector(id, 1f, Bytes(8))], ct);

        Assert.Equal(1, await temp.Store.ClearMessageVectorsAsync(model, ct));

        Assert.Equal(1, temp.Store.CountPendingVectors(model, ct));
        Assert.Equal("the roof leaks", temp.Store.GetBodyText(id, ct));
    }

    /// <summary>Vectors are derived from a message, so they go when it does.</summary>
    [Fact]
    public async Task DroppingTheAccountTakesItsVectorsWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var model = await temp.Store.EnsureVectorModelAsync(Model, ct);

        var id = await AddAsync(temp, folder, 1, "Roof repair", "the roof leaks", ct);
        await temp.Store.SetMessageVectorsAsync(model, [new MessageVector(id, 1f, Bytes(8))], ct);
        Assert.Equal(1, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM msg_vec", ct));

        await temp.Store.ForgetAccountAsync(account, ct);

        Assert.Equal(0, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM msg_vec", ct));
    }

    private static byte[] Bytes(int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) bytes[i] = (byte)(i + 1);
        return bytes;
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
