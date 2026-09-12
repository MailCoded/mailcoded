using System.Buffers.Binary;
using System.Text;
using Mailcoded.Core.Embedding;
using Xunit;

namespace Mailcoded.Core.Tests.Embedding;

/// <summary>A weights file is user-supplied, so every case here is a file an attacker could write.</summary>
public sealed class SafetensorsFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mailcoded-safetensors-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Reads_a_well_formed_float32_tensor()
    {
        var values = new[] { 1f, -2.5f, 3.25f, 0f, 7f, -0.125f };
        var path = Write(
            """{"embeddings":{"dtype":"F32","shape":[2,3],"data_offsets":[0,24]}}""",
            Float32Bytes(values));

        using var file = SafetensorsFile.Open(path);
        var tensor = file.Get("embeddings");

        Assert.Equal(TensorDType.Float32, tensor.DType);
        Assert.Equal(new[] { 2, 3 }, tensor.Shape);
        Assert.Equal(6, tensor.Count);
        Assert.Equal(values, file.ReadAll("embeddings"));
    }

    [Fact]
    public void Reads_float16_by_widening_it()
    {
        var half = new byte[4];
        BinaryPrimitives.WriteHalfLittleEndian(half, (Half)1.5f);
        BinaryPrimitives.WriteHalfLittleEndian(half.AsSpan(2), (Half)(-0.25f));
        var path = Write("""{"w":{"dtype":"F16","shape":[2],"data_offsets":[0,4]}}""", half);

        using var file = SafetensorsFile.Open(path);

        Assert.Equal(TensorDType.Float16, file.Get("w").DType);
        Assert.Equal(new[] { 1.5f, -0.25f }, file.ReadAll("w"));
    }

    [Fact]
    public void Ignores_the_metadata_key_without_treating_it_as_a_tensor()
    {
        var path = Write(
            """{"__metadata__":{"format":"pt"},"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]}}""",
            Float32Bytes([42f]));

        using var file = SafetensorsFile.Open(path);

        Assert.Equal(["w"], file.TensorNames);
    }

    public static TheoryData<string, string> HostileHeaders() => new()
    {
        { "offsets past the end of the file", """{"w":{"dtype":"F32","shape":[4],"data_offsets":[0,16]}}""" },
        { "an end before its start", """{"w":{"dtype":"F32","shape":[1],"data_offsets":[8,4]}}""" },
        { "a shape that disagrees with the bytes", """{"w":{"dtype":"F32","shape":[9],"data_offsets":[0,4]}}""" },
        { "a byte range that is not whole elements", """{"w":{"dtype":"F32","shape":[1],"data_offsets":[0,3]}}""" },
        { "an unsupported dtype", """{"w":{"dtype":"I64","shape":[1],"data_offsets":[0,8]}}""" },
        { "a negative dimension", """{"w":{"dtype":"F32","shape":[-1],"data_offsets":[0,4]}}""" },
        { "a negative offset", """{"w":{"dtype":"F32","shape":[1],"data_offsets":[-4,4]}}""" },
        { "one offset instead of two", """{"w":{"dtype":"F32","shape":[1],"data_offsets":[0]}}""" },
        { "three offsets", """{"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4,8]}}""" },
        { "no dtype", """{"w":{"shape":[1],"data_offsets":[0,4]}}""" },
        { "no shape", """{"w":{"dtype":"F32","data_offsets":[0,4]}}""" },
        { "no offsets", """{"w":{"dtype":"F32","shape":[1]}}""" },
        { "a tensor that is not an object", """{"w":[1,2,3]}""" },
        { "a header that is not an object", """[1,2,3]""" },
        { "a header that is not json at all", "not json" },
        { "a rank beyond the cap", """{"w":{"dtype":"F32","shape":[1,1,1,1,1,1,1,1,1],"data_offsets":[0,4]}}""" },
    };

    [Theory]
    [MemberData(nameof(HostileHeaders))]
    public void Refuses_a_header_with(string because, string header)
    {
        var path = Write(header, Float32Bytes([1f]));

        var thrown = Assert.Throws<InvalidDataException>(() => SafetensorsFile.Open(path));
        Assert.StartsWith("This is not a weights file mailcoded can read", thrown.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(because));
    }

    [Fact]
    public void Refuses_a_file_too_short_to_hold_a_header_length()
    {
        var path = Path.Combine(_directory, "stub.safetensors");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => SafetensorsFile.Open(path));
    }

    /// <summary>The header length is the one number that would be allocated before anything is known.</summary>
    [Fact]
    public void Refuses_an_absurd_header_length_without_allocating_it()
    {
        var path = Path.Combine(_directory, "huge.safetensors");
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, ulong.MaxValue);
        File.WriteAllBytes(path, bytes);

        var thrown = Assert.Throws<InvalidDataException>(() => SafetensorsFile.Open(path));
        Assert.Contains("over the", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_header_that_runs_past_the_end_of_the_file()
    {
        var path = Path.Combine(_directory, "truncated.safetensors");
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 4096);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => SafetensorsFile.Open(path));
    }

    [Fact]
    public void Refuses_more_tensors_than_the_cap_allows()
    {
        var entries = new List<string>();
        for (var i = 0; i < 6; i++)
            entries.Add($"\"w{i}\":{{\"dtype\":\"F32\",\"shape\":[1],\"data_offsets\":[0,4]}}");

        var path = Write("{" + string.Join(",", entries) + "}", Float32Bytes([1f]));

        var thrown = Assert.Throws<InvalidDataException>(() =>
            SafetensorsFile.Open(path, new SafetensorsLimits { MaxTensors = 3 }));
        Assert.Contains("more than 3 tensors", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A tensor name is attacker text that reaches an exception message.</summary>
    [Fact]
    public void Strips_control_characters_out_of_a_name_before_reporting_it()
    {
        // The escape must survive into the JSON source, so the name really does carry an ESC.
        var header = "{\"a\\u001b[31mb\":{\"dtype\":\"Q8\",\"shape\":[1],\"data_offsets\":[0,4]}}";
        var path = Write(header, Float32Bytes([1f]));

        var thrown = Assert.Throws<InvalidDataException>(() => SafetensorsFile.Open(path));

        Assert.DoesNotContain((char)0x1b, thrown.Message);
        Assert.Contains("a?[31mb", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_read_into_a_destination_of_the_wrong_size()
    {
        var path = Write("""{"w":{"dtype":"F32","shape":[2],"data_offsets":[0,8]}}""", Float32Bytes([1f, 2f]));
        using var file = SafetensorsFile.Open(path);

        Assert.Throws<ArgumentException>(() => file.Read(file.Get("w"), new float[3]));
    }

    [Fact]
    public void Refuses_to_read_after_it_is_disposed()
    {
        var path = Write("""{"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]}}""", Float32Bytes([1f]));
        var file = SafetensorsFile.Open(path);
        var tensor = file.Get("w");
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.Read(tensor, new float[1]));
    }

    [Fact]
    public void Names_a_tensor_that_is_not_there_rather_than_returning_nothing()
    {
        var path = Write("""{"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]}}""", Float32Bytes([1f]));
        using var file = SafetensorsFile.Open(path);

        Assert.False(file.TryGet("absent", out _));
        Assert.Throws<InvalidDataException>(() => file.Get("absent"));
    }

    private static byte[] Float32Bytes(ReadOnlySpan<float> values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private string Write(string header, byte[] data)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.safetensors");
        var headerBytes = Encoding.UTF8.GetBytes(header);
        var file = new byte[8 + headerBytes.Length + data.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(file, (ulong)headerBytes.Length);
        headerBytes.CopyTo(file.AsSpan(8));
        data.CopyTo(file.AsSpan(8 + headerBytes.Length));

        File.WriteAllBytes(path, file);
        return path;
    }
}
