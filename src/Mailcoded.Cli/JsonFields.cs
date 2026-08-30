using System.Globalization;
using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli;

/// <summary>Field writers shared by every document, so one shape never drifts from another.</summary>
internal static class JsonFields
{
    public const string IsoUtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(IsoUtcFormat, CultureInfo.InvariantCulture);

    public static void WriteDate(Utf8JsonWriter writer, string name, DateTimeOffset value) =>
        writer.WriteString(name, Iso(value));

    public static void WriteDate(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } present) writer.WriteString(name, Iso(present));
        else writer.WriteNull(name);
    }

    public static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    /// <summary>Server IMAP flags, in the wire vocabulary. Never merged with local Tags.</summary>
    public static void WriteFlags(Utf8JsonWriter writer, string name, MessageFlags flags)
    {
        writer.WriteStartArray(name);
        if (flags.HasFlag(MessageFlags.Unread)) writer.WriteStringValue("unread");
        if (flags.HasFlag(MessageFlags.Flagged)) writer.WriteStringValue("flagged");
        if (flags.HasFlag(MessageFlags.Answered)) writer.WriteStringValue("answered");
        if (flags.HasFlag(MessageFlags.Draft)) writer.WriteStringValue("draft");
        if (flags.HasFlag(MessageFlags.Deleted)) writer.WriteStringValue("deleted");
        if (flags.HasFlag(MessageFlags.Recent)) writer.WriteStringValue("recent");
        writer.WriteEndArray();
    }

    /// <summary>Local Tags. The vocabulary split from Flags is normative.</summary>
    public static void WriteTags(Utf8JsonWriter writer, string name, IReadOnlyList<Tag> tags)
    {
        writer.WriteStartArray(name);
        foreach (var tag in tags) writer.WriteStringValue(tag.Value);
        writer.WriteEndArray();
    }

    public static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    public static void WriteAddresses(Utf8JsonWriter writer, string name, IReadOnlyList<EmailAddress> addresses)
    {
        writer.WriteStartArray(name);
        foreach (var address in addresses) writer.WriteStringValue(address.Value);
        writer.WriteEndArray();
    }

    /// <summary>The two fields an agent branches on before deciding whether its context is complete.</summary>
    public static void WritePaging(Utf8JsonWriter writer, string? nextCursor, bool truncated)
    {
        writer.WriteBoolean("truncated", truncated);
        WriteText(writer, "next_cursor", nextCursor);
    }

    public static string Flags(MessageFlags flags)
    {
        var parts = new List<string>(6);
        if (flags.HasFlag(MessageFlags.Unread)) parts.Add("unread");
        if (flags.HasFlag(MessageFlags.Flagged)) parts.Add("flagged");
        if (flags.HasFlag(MessageFlags.Answered)) parts.Add("answered");
        if (flags.HasFlag(MessageFlags.Draft)) parts.Add("draft");
        if (flags.HasFlag(MessageFlags.Deleted)) parts.Add("deleted");
        if (flags.HasFlag(MessageFlags.Recent)) parts.Add("recent");
        return parts.Count == 0 ? "-" : string.Join(",", parts);
    }

    public static string Tags(IReadOnlyList<Tag> tags)
    {
        if (tags.Count == 0) return "-";

        var parts = new List<string>(tags.Count);
        foreach (var tag in tags) parts.Add(tag.Value);
        return string.Join(",", parts);
    }
}
