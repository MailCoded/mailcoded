using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mailcoded.Tui;

internal static class Json
{
    public static ReadOnlyMemory<byte> Serialize<T>(T value, JsonTypeInfo<T> info)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, info);
        writer.Flush();
        return buffer.WrittenMemory;
    }
}
