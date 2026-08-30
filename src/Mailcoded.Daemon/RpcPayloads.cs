using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mailcoded.Core.Protocol;

namespace Mailcoded.Daemon;

/// <summary>
/// Composes JSON-RPC 2.0 envelopes. Every value crosses the wire through the source-generated
/// <see cref="ProtocolJsonContext"/>; nothing here reflects over a type.
/// </summary>
internal static class RpcPayloads
{
    private const string JsonRpcProperty = "jsonrpc";
    private const string IdProperty = "id";
    private const string ResultProperty = "result";
    private const string ErrorProperty = "error";
    private const string MethodProperty = "method";
    private const string ParamsProperty = "params";

    /// <summary>Serializes one result DTO to its JSON value, ready to be embedded in a response.</summary>
    public static byte[] Value<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    public static byte[] Result(JsonElement? id, ReadOnlySpan<byte> resultJson)
    {
        var buffer = new ArrayBufferWriter<byte>(resultJson.Length + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(JsonRpcProperty, ProtocolConstants.JsonRpcVersion);
            WriteId(writer, id);
            writer.WritePropertyName(ResultProperty);
            writer.WriteRawValue(resultJson, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Error(JsonElement? id, JsonRpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(JsonRpcProperty, ProtocolConstants.JsonRpcVersion);
            WriteId(writer, id);
            writer.WritePropertyName(ErrorProperty);
            JsonSerializer.Serialize(writer, error, ProtocolJsonContext.Default.JsonRpcError);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Notification<T>(string method, T parameters, JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(JsonRpcProperty, ProtocolConstants.JsonRpcVersion);
            writer.WriteString(MethodProperty, method);
            writer.WritePropertyName(ParamsProperty);
            JsonSerializer.Serialize(writer, parameters, typeInfo);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The id is echoed verbatim: it may be a number, a string, or absent.</summary>
    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
    {
        writer.WritePropertyName(IdProperty);
        if (id is { } value) value.WriteTo(writer);
        else writer.WriteNullValue();
    }
}
