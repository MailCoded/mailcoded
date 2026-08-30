using System.Buffers;
using System.Security.Cryptography;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string SelectBlob = "SELECT id, sha256, ext_path, LENGTH(bytes) FROM blobs";
    private const int CopyBufferSize = 81_920;

    public static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Stores raw message bytes content-addressed by sha256. Re-storing identical content returns
    /// the existing row, which is what keeps a UIDVALIDITY flip from duplicating blobs (edge case 1).
    /// </summary>
    public Task<BlobRef> StoreBlobAsync(byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var sha = ComputeSha256(content);
        long size = content.LongLength;

        if (size > _options.InlineBlobThresholdBytes)
        {
            var path = WriteExternalBlob(sha, content);
            return StoreBlobRowAsync(sha, null, path, size, ct);
        }

        return StoreBlobRowAsync(sha, content, null, size, ct);
    }

    /// <summary>
    /// Streams <paramref name="source"/> to a content-addressed file while hashing it, so a 100 MB
    /// attachment is never materialized as one array (edge case 14, RELIABILITY §14.2).
    /// </summary>
    public async Task<BlobRef> StoreBlobAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        Directory.CreateDirectory(BlobDirectory);
        var stagingPath = Path.Combine(BlobDirectory, "staging-" + Guid.NewGuid().ToString("N") + ".tmp");

        string sha;
        long size = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var staging = new FileStream(
                             stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             CopyBufferSize, useAsync: true))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await staging.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    size += read;
                }
            }

            sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            TryDeleteFile(stagingPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        try
        {
            if (size <= _options.InlineBlobThresholdBytes)
            {
                var inline = await File.ReadAllBytesAsync(stagingPath, ct).ConfigureAwait(false);
                return await StoreBlobRowAsync(sha, inline, null, size, ct).ConfigureAwait(false);
            }

            var finalPath = StorePaths.ExternalBlobPath(BlobDirectory, sha);
            var directory = Path.GetDirectoryName(finalPath);
            if (directory is not null) Directory.CreateDirectory(directory);

            if (File.Exists(finalPath)) TryDeleteFile(stagingPath);
            else File.Move(stagingPath, finalPath, overwrite: false);

            return await StoreBlobRowAsync(sha, null, finalPath, size, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(stagingPath);
        }
    }

    public BlobRef? FindBlob(string sha256Hex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sha256Hex);
        return Read<BlobRef?>(session =>
        {
            using var reader = session
                .Prepare(SelectBlob + " WHERE sha256 = $sha", "$sha")
                .SetText(0, sha256Hex)
                .ExecuteReader();
            return reader.Read() ? MapBlob(reader) : null;
        }, ct);
    }

    public BlobRef? GetBlob(BlobId id, CancellationToken ct = default) =>
        Read<BlobRef?>(session =>
        {
            using var reader = session
                .Prepare(SelectBlob + " WHERE id = $id", "$id")
                .SetInt(0, id.Value)
                .ExecuteReader();
            return reader.Read() ? MapBlob(reader) : null;
        }, ct);

    /// <summary>Reads a blob whole. Prefer <see cref="OpenBlob"/> for anything attachment-sized.</summary>
    public byte[]? ReadBlobBytes(BlobId id, CancellationToken ct = default)
    {
        var located = Read<(byte[]? Inline, string? Path)?>(session =>
        {
            using var reader = session
                .Prepare("SELECT bytes, ext_path FROM blobs WHERE id = $id", "$id")
                .SetInt(0, id.Value)
                .ExecuteReader();
            if (!reader.Read()) return null;
            return (Db.Blob(reader, 0), Db.Str(reader, 1));
        }, ct);

        if (located is not { } blob) return null;
        if (blob.Inline is { } inline) return inline;
        return blob.Path is { } path && File.Exists(path) ? File.ReadAllBytes(path) : (byte[]?)null;
    }

    /// <summary>Opens a blob for streaming; the caller owns the returned stream.</summary>
    public Stream? OpenBlob(BlobId id, CancellationToken ct = default)
    {
        var located = Read<(byte[]? Inline, string? Path)?>(session =>
        {
            using var reader = session
                .Prepare("SELECT bytes, ext_path FROM blobs WHERE id = $id", "$id")
                .SetInt(0, id.Value)
                .ExecuteReader();
            if (!reader.Read()) return null;
            return (Db.Blob(reader, 0), Db.Str(reader, 1));
        }, ct);

        if (located is not { } blob) return null;
        if (blob.Inline is { } inline) return new MemoryStream(inline, writable: false);
        if (blob.Path is not { } path || !File.Exists(path)) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
    }

    public Task LinkBlobAsync(LocalMessageId messageId, BlobId blobId, CancellationToken ct) =>
        WriteAsync(context => context.Session
            .Prepare("UPDATE messages SET blob_id = $blob WHERE id = $id", "$blob", "$id")
            .SetInt(0, blobId.Value)
            .SetInt(1, messageId.Value)
            .Execute(), ct);

    /// <summary>
    /// Links an already-stored blob by content hash without re-downloading it. Returns null when
    /// the content is not in the store yet.
    /// </summary>
    public Task<BlobId?> TryLinkBlobBySha256Async(LocalMessageId messageId, string sha256Hex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sha256Hex);

        return WriteAsync<BlobId?>(context =>
        {
            var existing = context.Session
                .Prepare("SELECT id FROM blobs WHERE sha256 = $sha", "$sha")
                .SetText(0, sha256Hex)
                .ExecuteNullableInt64();

            if (existing is not { } id) return null;

            context.Session
                .Prepare("UPDATE messages SET blob_id = $blob WHERE id = $id", "$blob", "$id")
                .SetInt(0, id)
                .SetInt(1, messageId.Value)
                .Execute();

            return new BlobId(id);
        }, ct);
    }

    private Task<BlobRef> StoreBlobRowAsync(string sha, byte[]? inline, string? externalPath, long size, CancellationToken ct) =>
        WriteAsync(context =>
        {
            var existing = context.Session
                .Prepare("SELECT id FROM blobs WHERE sha256 = $sha", "$sha")
                .SetText(0, sha)
                .ExecuteNullableInt64();

            if (existing is { } found)
                return new BlobRef { Id = new BlobId(found), Sha256 = sha, Size = size, ExternalPath = externalPath };

            var id = context.Session
                .Prepare("INSERT INTO blobs (sha256, bytes, ext_path) VALUES ($sha,$bytes,$path) RETURNING id",
                    "$sha", "$bytes", "$path")
                .SetText(0, sha)
                .SetBlob(1, inline)
                .SetText(2, externalPath)
                .ExecuteInt64();

            return new BlobRef { Id = new BlobId(id), Sha256 = sha, Size = size, ExternalPath = externalPath };
        }, ct);

    private string WriteExternalBlob(string sha, byte[] content)
    {
        var path = StorePaths.ExternalBlobPath(BlobDirectory, sha);
        var directory = Path.GetDirectoryName(path);
        if (directory is not null) Directory.CreateDirectory(directory);

        if (File.Exists(path)) return path;

        var staging = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(staging, content);
            File.Move(staging, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer won the race; the content is identical by construction.
            TryDeleteFile(staging);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDeleteFile(staging);
            throw new StoreException(FailureCategory.Full, "The blob store directory is not writable.", ex);
        }

        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static BlobRef MapBlob(SqliteDataReader reader)
    {
        var externalPath = Db.Str(reader, 2);
        var size = externalPath is not null && File.Exists(externalPath)
            ? new FileInfo(externalPath).Length
            : Db.Int(reader, 3);

        return new BlobRef
        {
            Id = new BlobId(reader.GetInt64(0)),
            Sha256 = reader.GetString(1),
            ExternalPath = externalPath,
            Size = size,
        };
    }
}
