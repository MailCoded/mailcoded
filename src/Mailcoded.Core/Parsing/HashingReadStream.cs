using System.Buffers;
using System.Security.Cryptography;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// Forward-only tee that hashes every byte the MIME parser consumes, so a message is parsed and
/// its blob identity computed in a single pass over the source stream. Reports
/// <see cref="CanSeek"/> = false deliberately: a seek would break the digest.
/// </summary>
internal sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;
    private long _read;
    private bool _disposed;

    public HashingReadStream(Stream inner)
    {
        _inner = inner;
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    public long BytesRead => _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0)
        {
            _hash.AppendData(buffer.AsSpan(offset, n));
            _read += n;
        }

        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        if (n > 0)
        {
            _hash.AppendData(buffer[..n]);
            _read += n;
        }

        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (n > 0)
        {
            _hash.AppendData(buffer.Span[..n]);
            _read += n;
        }

        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    /// <summary>Consumes anything the parser left behind so the digest covers the whole raw message.</summary>
    public void DrainRemainder(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RawMessageHasher.BufferSize);
        try
        {
            while (Read(buffer, 0, buffer.Length) > 0) ct.ThrowIfCancellationRequested();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task DrainRemainderAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RawMessageHasher.BufferSize);
        try
        {
            while (await ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) > 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public string GetHashHex() => RawMessageHasher.ToHexCurrent(_hash);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
