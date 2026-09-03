using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Protocol;
using Mailcoded.Core.Store;

namespace Mailcoded.Mcp;

/// <summary>Domain-to-JSON shaping only. Vocabulary stays normative: Tag is local, Flag is server.</summary>
internal static class Wire
{
    private const string IsoUtc = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Date(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(IsoUtc, CultureInfo.InvariantCulture);

    public static string? DateOrNull(DateTimeOffset? value) => value is { } moment ? Date(moment) : null;

    public static IReadOnlyList<string> Flags(MessageFlags flags)
    {
        if (flags == MessageFlags.None) return [];

        var names = new List<string>(6);
        if ((flags & MessageFlags.Unread) != 0) names.Add("unread");
        if ((flags & MessageFlags.Flagged) != 0) names.Add("flagged");
        if ((flags & MessageFlags.Answered) != 0) names.Add("answered");
        if ((flags & MessageFlags.Draft) != 0) names.Add("draft");
        if ((flags & MessageFlags.Deleted) != 0) names.Add("deleted");
        if ((flags & MessageFlags.Recent) != 0) names.Add("recent");
        return names;
    }

    public static IReadOnlyList<string> Tags(IReadOnlyList<Tag> tags)
    {
        if (tags.Count == 0) return [];

        var names = new List<string>(tags.Count);
        foreach (var tag in tags) names.Add(tag.Value);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public static IReadOnlyList<string> Addresses(IReadOnlyList<EmailAddress> addresses)
    {
        if (addresses.Count == 0) return [];

        var values = new List<string>(addresses.Count);
        foreach (var address in addresses) values.Add(address.Value);
        return values;
    }

    public static string Connection(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => HealthStates.Connecting,
        ConnectionState.Connected => HealthStates.Connected,
        ConnectionState.AuthRequired => HealthStates.AuthRequired,
        ConnectionState.Error => HealthStates.Error,
        _ => HealthStates.Disconnected,
    };

    public static string Route(SearchRoute route) => route switch
    {
        SearchRoute.Fts => "fts",
        SearchRoute.Cjk => "cjk",
        SearchRoute.Like => "like",
        _ => "none",
    };

    public static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0) return string.Empty;
        return value.Length <= maxChars ? value : value[..maxChars];
    }
}
