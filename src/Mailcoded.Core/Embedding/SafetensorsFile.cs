using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Mailcoded.Core.Embedding;

public enum TensorDType
{
    Float32,
    Float16,
}

/// <summary>One tensor's position and shape. Holds no bytes.</summary>
public sealed record TensorInfo
{
    public required string Name { get; init; }

    public required TensorDType DType { get; init; }

    public required IReadOnlyList<int> Shape { get; init; }

    /// <summary>Element count, already proven to agree with the declared byte length.</summary>
    public required int Count { get; init; }

    internal long ByteOffset { get; init; }

    internal long ByteLength { get; init; }
}

/// <summary>Caps applied before anything is allocated or read.</summary>
public sealed record SafetensorsLimits
{
    public int MaxHeaderBytes { get; init; } = 16 * 1024 * 1024;

    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public int MaxTensors { get; init; } = 4096;

    public int MaxTensorElements { get; init; } = 256 * 1024 * 1024;

    public int MaxRank { get; init; } = 8;

    public static readonly SafetensorsLimits Default = new();
}

/// <summary>A user-supplied weights file, and therefore untrusted: every offset is proven to lie inside
/// the file before a read, nothing is sized from a value the file chose, and bytes are copied into a
/// buffer the caller owns rather than mapped into a span.</summary>
public sealed class SafetensorsFile : IDisposable
{
    private const int HeaderLengthBytes = 8;

    private readonly SafeFileHandle _handle;
    private readonly Dictionary<string, TensorInfo> _tensors;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private bool _disposed;

    private SafetensorsFile(
        SafeFileHandle handle,
        Dictionary<string, TensorInfo> tensors,
        long dataStart,
        long dataLength)
    {
        _handle = handle;
        _tensors = tensors;
        _dataStart = dataStart;
        _dataLength = dataLength;
    }

    public IReadOnlyCollection<string> TensorNames => _tensors.Keys;

    public static SafetensorsFile Open(string path, SafetensorsLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var caps = limits ?? SafetensorsLimits.Default;

        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var fileLength = RandomAccess.GetLength(handle);
            if (fileLength > caps.MaxFileBytes)
            {
                throw Invalid($"the file is {Bytes(fileLength)} bytes, over the {Bytes(caps.MaxFileBytes)} cap");
            }
            if (fileLength < HeaderLengthBytes)
            {
                throw Invalid("the file is too short to hold a header length");
            }

            Span<byte> lengthBytes = stackalloc byte[HeaderLengthBytes];
            ReadExactly(handle, lengthBytes, 0);
            var headerLength = BinaryPrimitives.ReadUInt64LittleEndian(lengthBytes);

            // Checked before it is used as a size, which is the whole point of reading it separately.
            if (headerLength > (ulong)caps.MaxHeaderBytes)
            {
                throw Invalid($"the header claims {headerLength} bytes, over the {Bytes(caps.MaxHeaderBytes)} cap");
            }
            var dataStart = HeaderLengthBytes + (long)headerLength;
            if (dataStart > fileLength)
            {
                throw Invalid("the header runs past the end of the file");
            }

            var header = new byte[(int)headerLength];
            ReadExactly(handle, header, HeaderLengthBytes);

            var dataLength = fileLength - dataStart;

            // A malformed header must arrive as one recognisable failure, not as whatever the JSON
            // reader happens to throw from wherever it gave up.
            Dictionary<string, TensorInfo> tensors;
            try
            {
                tensors = ParseHeader(header, dataLength, caps);
            }
            catch (JsonException ex)
            {
                throw Invalid($"the header is not readable JSON ({Describe(ex.Message)})");
            }

            return new SafetensorsFile(handle, tensors, dataStart, dataLength);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool TryGet(string name, out TensorInfo tensor) => _tensors.TryGetValue(name, out tensor!);

    public TensorInfo Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _tensors.TryGetValue(name, out var tensor)
            ? tensor
            : throw Invalid($"no tensor named '{Describe(name)}'");
    }

    /// <summary>Copies one tensor into a destination the caller sized from <c>Count</c>.</summary>
    public void Read(TensorInfo tensor, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (destination.Length != tensor.Count)
        {
            throw new ArgumentException(
                $"'{Describe(tensor.Name)}' holds {tensor.Count} elements, not {destination.Length}.",
                nameof(destination));
        }

        var offset = _dataStart + tensor.ByteOffset;
        if (tensor.DType == TensorDType.Float32)
        {
            ReadExactly(_handle, MemoryMarshal.AsBytes(destination), offset);
            return;
        }

        var half = new byte[tensor.ByteLength];
        ReadExactly(_handle, half, offset);
        var source = MemoryMarshal.Cast<byte, Half>(half);
        for (var i = 0; i < destination.Length; i++) destination[i] = (float)source[i];
    }

    public float[] ReadAll(string name)
    {
        var tensor = Get(name);
        var values = new float[tensor.Count];
        Read(tensor, values);
        return values;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    private static Dictionary<string, TensorInfo> ParseHeader(
        ReadOnlySpan<byte> header,
        long dataLength,
        SafetensorsLimits caps)
    {
        var reader = new Utf8JsonReader(header, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw Invalid("the header is not a JSON object");
        }

        var tensors = new Dictionary<string, TensorInfo>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString() ?? throw Invalid("a tensor name is null");
            if (!reader.Read()) throw Invalid($"'{Describe(name)}' has no value");

            if (string.Equals(name, "__metadata__", StringComparison.Ordinal))
            {
                reader.Skip();
                continue;
            }
            if (tensors.Count >= caps.MaxTensors)
            {
                throw Invalid($"the header declares more than {caps.MaxTensors} tensors");
            }

            tensors.Add(name, ReadTensor(ref reader, name, dataLength, caps));
        }

        if (reader.TokenType != JsonTokenType.EndObject) throw Invalid("the header object is unterminated");
        return tensors;
    }

    private static TensorInfo ReadTensor(
        ref Utf8JsonReader reader,
        string name,
        long dataLength,
        SafetensorsLimits caps)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Invalid($"'{Describe(name)}' is not an object");

        string? dtype = null;
        List<int>? shape = null;
        long start = -1;
        long end = -1;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = reader.GetString();
            if (!reader.Read()) throw Invalid($"'{Describe(name)}' is truncated");

            switch (field)
            {
                case "dtype":
                    dtype = reader.GetString();
                    break;
                case "shape":
                    shape = ReadShape(ref reader, name, caps);
                    break;
                case "data_offsets":
                    (start, end) = ReadOffsets(ref reader, name);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (dtype is null || shape is null || start < 0) throw Invalid($"'{Describe(name)}' is missing a field");

        var elementSize = dtype switch
        {
            "F32" => 4,
            "F16" => 2,
            _ => throw Invalid($"'{Describe(name)}' has dtype '{Describe(dtype)}'; only F32 and F16 are read"),
        };

        if (end < start) throw Invalid($"'{Describe(name)}' ends before it starts");
        if (end > dataLength) throw Invalid($"'{Describe(name)}' runs {end - dataLength} bytes past the end of the file");

        var byteLength = end - start;
        if (byteLength % elementSize != 0) throw Invalid($"'{Describe(name)}' is not a whole number of elements");

        var count = byteLength / elementSize;
        if (count > caps.MaxTensorElements) throw Invalid($"'{Describe(name)}' holds more than {caps.MaxTensorElements} elements");

        long declared = 1;
        foreach (var dimension in shape) declared *= dimension;
        if (declared != count) throw Invalid($"'{Describe(name)}' declares a shape of {declared} elements but {count} bytes' worth");

        return new TensorInfo
        {
            Name = name,
            DType = elementSize == 4 ? TensorDType.Float32 : TensorDType.Float16,
            Shape = shape,
            Count = (int)count,
            ByteOffset = start,
            ByteLength = byteLength,
        };
    }

    private static List<int> ReadShape(ref Utf8JsonReader reader, string name, SafetensorsLimits caps)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Invalid($"'{Describe(name)}' has a non-array shape");

        var shape = new List<int>(4);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var dimension) || dimension < 0)
            {
                throw Invalid($"'{Describe(name)}' has a shape that is not a list of non-negative integers");
            }
            if (shape.Count >= caps.MaxRank) throw Invalid($"'{Describe(name)}' has a rank above {caps.MaxRank}");
            shape.Add(dimension);
        }

        return shape;
    }

    private static (long Start, long End) ReadOffsets(ref Utf8JsonReader reader, string name)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Invalid($"'{Describe(name)}' has non-array data_offsets");

        Span<long> pair = stackalloc long[2];
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (seen == 2) throw Invalid($"'{Describe(name)}' has more than two data_offsets");
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value) || value < 0)
            {
                throw Invalid($"'{Describe(name)}' has a data_offset that is not a non-negative integer");
            }
            pair[seen++] = value;
        }

        if (seen != 2) throw Invalid($"'{Describe(name)}' does not have two data_offsets");
        return (pair[0], pair[1]);
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var got = RandomAccess.Read(handle, destination[read..], offset + read);
            if (got == 0) throw Invalid("the file ended before a tensor did");
            read += got;
        }
    }

    /// <summary>A name out of the file reaches an exception message, so it is bounded and stripped first.</summary>
    private static string Describe(string value)
    {
        const int max = 64;
        var kept = new char[Math.Min(value.Length, max)];
        var length = 0;
        foreach (var character in value)
        {
            if (length == kept.Length) break;
            kept[length++] = char.IsControl(character) ? '?' : character;
        }

        return length == value.Length ? new string(kept, 0, length) : new string(kept, 0, length) + "…";
    }

    private static string Bytes(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static InvalidDataException Invalid(string reason) =>
        new($"This is not a weights file mailcoded can read: {reason}.");
}
