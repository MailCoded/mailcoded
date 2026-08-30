using System.Buffers;
using System.Text;
using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Store;

/// <summary>Hand-written codec: no reflection, nothing for AOT to trim.</summary>
internal static class OutboxEnvelopeJson
{
    public static string Write(OutboxEnvelope envelope)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            if (!string.IsNullOrEmpty(envelope.From.Value)) writer.WriteString("from", envelope.From.Value);
            WriteAddresses(writer, "to", envelope.To);
            WriteAddresses(writer, "cc", envelope.Cc);
            WriteAddresses(writer, "bcc", envelope.Bcc);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static OutboxEnvelope? Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var from = default(EmailAddress);
            if (root.TryGetProperty("from", out var fromElement)
                && fromElement.ValueKind == JsonValueKind.String
                && EmailAddress.TryParse(fromElement.GetString(), out var parsedFrom))
            {
                from = parsedFrom;
            }

            return new OutboxEnvelope
            {
                From = from,
                To = ReadAddresses(root, "to"),
                Cc = ReadAddresses(root, "cc"),
                Bcc = ReadAddresses(root, "bcc"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteAddresses(Utf8JsonWriter writer, string name, IReadOnlyList<EmailAddress> addresses)
    {
        if (addresses.Count == 0) return;

        writer.WriteStartArray(name);
        foreach (var address in addresses)
            if (!string.IsNullOrEmpty(address.Value))
                writer.WriteStringValue(address.Value);
        writer.WriteEndArray();
    }

    private static IReadOnlyList<EmailAddress> ReadAddresses(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];

        var addresses = new List<EmailAddress>();
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && EmailAddress.TryParse(item.GetString(), out var address))
                addresses.Add(address);

        return addresses;
    }
}
