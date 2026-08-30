using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class BlobStoreTests
{
    private const int InlineThreshold = 512 * 1024;

    [Fact]
    public async Task ContentAtTheThresholdStaysInline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var content = Payload(InlineThreshold, seed: 1);
        var blob = await temp.Store.StoreBlobAsync(content, ct);

        Assert.False(blob.IsExternal);
        Assert.Null(blob.ExternalPath);
        Assert.Empty(Directory.GetFiles(temp.Store.BlobDirectory, "*.blob", SearchOption.AllDirectories));

        var stored = Require.Ref(temp.Store.FindBlob(blob.Sha256, ct), "the stored blob");
        Assert.False(stored.IsExternal);
        Assert.Equal((long)InlineThreshold, stored.Size);
        Assert.Equal(content, temp.Store.ReadBlobBytes(blob.Id, ct));
    }

    [Fact]
    public async Task ContentAboveTheThresholdBecomesAContentAddressedFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var content = Payload(InlineThreshold + 1, seed: 2);
        var blob = await temp.Store.StoreBlobAsync(content, ct);

        Assert.True(blob.IsExternal);
        var path = Require.Ref(blob.ExternalPath, "the external path");
        Assert.Equal(StorePaths.ExternalBlobPath(temp.Store.BlobDirectory, blob.Sha256), path);
        Assert.True(File.Exists(path));
        Assert.Equal((long)InlineThreshold + 1, new FileInfo(path).Length);

        var name = Path.GetFileNameWithoutExtension(path);
        Assert.Equal(64, name.Length);
        foreach (var c in name) Assert.True(char.IsAsciiLetterOrDigit(c), $"'{c}' is not hex-safe on NTFS.");

        Assert.Equal(
            1L,
            StoreQuery.Scalar(
                temp.Store,
                "SELECT COUNT(*) FROM blobs WHERE bytes IS NULL AND ext_path IS NOT NULL",
                ct));

        Assert.Equal(content, temp.Store.ReadBlobBytes(blob.Id, ct));
    }

    [Fact]
    public async Task StoringIdenticalContentTwiceKeepsOneBlobRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var small = Payload(1024, seed: 3);
        var large = Payload(InlineThreshold + 4096, seed: 4);

        var firstSmall = await temp.Store.StoreBlobAsync(small, ct);
        var secondSmall = await temp.Store.StoreBlobAsync(small, ct);
        var firstLarge = await temp.Store.StoreBlobAsync(large, ct);
        var secondLarge = await temp.Store.StoreBlobAsync(large, ct);

        Assert.Equal(firstSmall.Id, secondSmall.Id);
        Assert.Equal(firstLarge.Id, secondLarge.Id);
        Assert.Equal(2L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM blobs", ct));
        Assert.Single(Directory.GetFiles(temp.Store.BlobDirectory, "*.blob", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StreamingAndBufferedStoresAgreeOnIdentityAndPlacement()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var content = Payload(InlineThreshold + 777, seed: 5);
        var buffered = await temp.Store.StoreBlobAsync(content, ct);

        using var source = new MemoryStream(content, writable: false);
        var streamed = await temp.Store.StoreBlobAsync(source, ct);

        Assert.Equal(buffered.Sha256, streamed.Sha256);
        Assert.Equal(buffered.Id, streamed.Id);
        Assert.Equal(1L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM blobs", ct));
        Assert.Empty(Directory.GetFiles(temp.Store.BlobDirectory, "staging-*.tmp"));
    }

    [Fact]
    public async Task StreamingSmallContentStillLandsInline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var content = Payload(4096, seed: 6);
        using var source = new MemoryStream(content, writable: false);
        var blob = await temp.Store.StoreBlobAsync(source, ct);

        Assert.False(blob.IsExternal);
        Assert.Equal(SqliteStore.ComputeSha256(content), blob.Sha256);
        Assert.Equal(content, temp.Store.ReadBlobBytes(blob.Id, ct));
        Assert.Empty(Directory.GetFiles(temp.Store.BlobDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OpenBlobStreamsBothShapes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var inline = Payload(2048, seed: 7);
        var external = Payload(InlineThreshold + 2048, seed: 8);

        var inlineBlob = await temp.Store.StoreBlobAsync(inline, ct);
        var externalBlob = await temp.Store.StoreBlobAsync(external, ct);

        await using (var stream = Require.Ref(temp.Store.OpenBlob(inlineBlob.Id, ct), "the inline blob stream"))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            Assert.Equal(inline, buffer.ToArray());
        }

        await using (var stream = Require.Ref(temp.Store.OpenBlob(externalBlob.Id, ct), "the external blob stream"))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            Assert.Equal(external, buffer.ToArray());
        }

        Assert.Null(temp.Store.OpenBlob(new BlobId(9999), ct));
    }

    private static byte[] Payload(int size, int seed)
    {
        var bytes = new byte[size];
        var random = new Random(seed);
        random.NextBytes(bytes);
        return bytes;
    }
}
