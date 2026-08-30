using System.Buffers;
using System.Security.Cryptography;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// SHA-256 of a raw RFC822 message, lower-case hex. This is blob identity for the store, so the
/// encoding is load-bearing: it must stay lower-case hex forever.
/// </summary>
public static class RawMessageHasher
{
    /// <summary>Above the 85,000-byte LOH threshold, so the buffer is always rented rather than allocated.</summary>
    internal const int BufferSize = 131_072;

    public static string ComputeSha256Hex(Stream raw, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = raw.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ToHex(hash);
    }

    public static async Task<string> ComputeSha256HexAsync(Stream raw, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await raw.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ToHex(hash);
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> raw)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(raw, digest);
        return Convert.ToHexStringLower(digest);
    }

    internal static string ToHex(IncrementalHash hash)
    {
        Span<byte> digest = stackalloc byte[32];
        var written = hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest[..written]);
    }

    internal static string ToHexCurrent(IncrementalHash hash)
    {
        Span<byte> digest = stackalloc byte[32];
        var written = hash.GetCurrentHash(digest);
        return Convert.ToHexStringLower(digest[..written]);
    }
}
