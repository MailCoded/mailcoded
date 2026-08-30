using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Tags;

/// <summary>The bidirectional Tag/Flag mapping and the conflict rules. Pure, no I/O, no ambient time.</summary>
public static class TagFlagMap
{
    public const string SeenFlag = "\\Seen";
    public const string FlaggedFlag = "\\Flagged";
    public const string AnsweredFlag = "\\Answered";
    public const string DraftFlag = "\\Draft";
    public const string DeletedFlag = "\\Deleted";

    /// <summary>The system flags that round-trip through a tag; everything else is folder or session state.</summary>
    public const MessageFlags MappedFlags =
        MessageFlags.Unread | MessageFlags.Flagged | MessageFlags.Answered | MessageFlags.Draft;

    /// <summary>Tags describing local structure; pushing them as IMAP keywords would litter the server.</summary>
    public static readonly IReadOnlyList<Tag> LocalOnlyTags = [Tag.Inbox];

    public static bool IsLocalOnly(Tag tag) => tag.Value is "inbox";

    /// <summary>The system flag a tag stands for, or null when the tag is a custom keyword.</summary>
    public static MessageFlags? SystemFlagFor(Tag tag) => tag.Value switch
    {
        "unread" => MessageFlags.Unread,
        "flagged" => MessageFlags.Flagged,
        "replied" => MessageFlags.Answered,
        "draft" => MessageFlags.Draft,
        _ => null,
    };

    public static Tag? TagForSystemFlag(MessageFlags flag) => flag switch
    {
        MessageFlags.Unread => Tag.Unread,
        MessageFlags.Flagged => Tag.Flagged,
        MessageFlags.Answered => Tag.Replied,
        MessageFlags.Draft => Tag.Draft,
        _ => null,
    };

    /// <summary>A server keyword becomes a same-name tag; a system flag is never a keyword.</summary>
    public static bool TryTagForKeyword(string? keyword, out Tag tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(keyword)) return false;

        var k = keyword.Trim();
        if (k[0] == '\\') return false;
        if (!Tag.TryParse(k, out var parsed)) return false;
        if (SystemFlagFor(parsed) is not null) return false;

        tag = parsed;
        return true;
    }

    /// <summary>IMAP keywords are case-insensitive atoms, so the lower-cased tag value is the wire form.</summary>
    public static string ToKeyword(Tag tag) => tag.Value;

    /// <summary>The system tags implied by a flag bitfield; <c>unread</c> is the absence of <c>\Seen</c>.</summary>
    public static IReadOnlyList<Tag> SystemTagsOf(MessageFlags flags)
    {
        var tags = new List<Tag>(4);
        if ((flags & MessageFlags.Unread) != 0) tags.Add(Tag.Unread);
        if ((flags & MessageFlags.Flagged) != 0) tags.Add(Tag.Flagged);
        if ((flags & MessageFlags.Answered) != 0) tags.Add(Tag.Replied);
        if ((flags & MessageFlags.Draft) != 0) tags.Add(Tag.Draft);
        return tags;
    }

    /// <summary>The full tag set a message presents: system tags from the flags plus custom keywords.</summary>
    public static IReadOnlyList<Tag> ToTags(MessageFlags flags, IReadOnlyList<string>? keywords)
    {
        var set = new HashSet<Tag>();
        foreach (var t in SystemTagsOf(flags)) set.Add(t);

        if (keywords is not null)
            foreach (var k in keywords)
                if (TryTagForKeyword(k, out var t)) set.Add(t);

        return Sorted(set);
    }

    /// <summary>The system flags implied by a tag set. Custom tags contribute nothing.</summary>
    public static MessageFlags ToFlags(IReadOnlyList<Tag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var flags = MessageFlags.None;
        foreach (var t in tags)
            if (SystemFlagFor(t) is { } f) flags |= f;
        return flags;
    }

    /// <summary>The custom keywords implied by a tag set, in a stable order.</summary>
    public static IReadOnlyList<string> ToKeywords(IReadOnlyList<Tag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var set = new HashSet<Tag>();
        foreach (var t in tags)
            if (SystemFlagFor(t) is null && !IsLocalOnly(t)) set.Add(t);

        var sorted = Sorted(set);
        var keywords = new List<string>(sorted.Count);
        foreach (var t in sorted) keywords.Add(ToKeyword(t));
        return keywords;
    }

    /// <summary>Projects a tag request onto server flags; a server that drops keywords gets none.</summary>
    public static FlagDelta Project(TagDelta delta, bool serverAcceptsCustomKeywords)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var add = MessageFlags.None;
        var remove = MessageFlags.None;
        var addKeywords = new List<string>();
        var removeKeywords = new List<string>();

        foreach (var t in delta.Add)
        {
            if (SystemFlagFor(t) is { } f) { add |= f; remove &= ~f; continue; }
            if (!serverAcceptsCustomKeywords || IsLocalOnly(t)) continue;

            var k = ToKeyword(t);
            if (!addKeywords.Contains(k)) addKeywords.Add(k);
        }

        // A tag named in both lists resolves to Remove: the destructive intent is the safe reading.
        foreach (var t in delta.Remove)
        {
            if (SystemFlagFor(t) is { } f) { remove |= f; add &= ~f; continue; }
            if (!serverAcceptsCustomKeywords || IsLocalOnly(t)) continue;

            var k = ToKeyword(t);
            addKeywords.Remove(k);
            if (!removeKeywords.Contains(k)) removeKeywords.Add(k);
        }

        addKeywords.Sort(StringComparer.Ordinal);
        removeKeywords.Sort(StringComparer.Ordinal);

        return new FlagDelta
        {
            Add = add,
            Remove = remove,
            AddKeywords = addKeywords,
            RemoveKeywords = removeKeywords,
        };
    }

    /// <summary>Applies a tag delta to a tag set. Remove wins when a tag appears in both lists.</summary>
    public static IReadOnlyList<Tag> Apply(IReadOnlyList<Tag> current, TagDelta delta)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(delta);

        var set = new HashSet<Tag>(current);
        foreach (var t in delta.Add) set.Add(t);
        foreach (var t in delta.Remove) set.Remove(t);
        return Sorted(set);
    }

    /// <summary>Splits a delta into flag names to add and to clear; adding Unread means clearing <c>\Seen</c>.</summary>
    public static (IReadOnlyList<string> Set, IReadOnlyList<string> Clear) ToServerFlagNames(FlagDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var set = new List<string>();
        var clear = new List<string>();

        if ((delta.Add & MessageFlags.Unread) != 0) clear.Add(SeenFlag);
        if ((delta.Remove & MessageFlags.Unread) != 0) set.Add(SeenFlag);

        AddPair(delta, MessageFlags.Flagged, FlaggedFlag, set, clear);
        AddPair(delta, MessageFlags.Answered, AnsweredFlag, set, clear);
        AddPair(delta, MessageFlags.Draft, DraftFlag, set, clear);
        AddPair(delta, MessageFlags.Deleted, DeletedFlag, set, clear);

        set.AddRange(delta.AddKeywords);
        clear.AddRange(delta.RemoveKeywords);
        return (set, clear);
    }

    private static void AddPair(FlagDelta delta, MessageFlags bit, string name, List<string> set, List<string> clear)
    {
        if ((delta.Add & bit) != 0) set.Add(name);
        if ((delta.Remove & bit) != 0) clear.Add(name);
    }

    /// <summary>Server wins on IMAP flags, local wins on custom tags; also returns the delta to push back.</summary>
    public static TagMergeResult Merge(TagMergeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var flags = input.ServerFlags;

        var serverCustom = new HashSet<Tag>();
        foreach (var k in input.ServerKeywords)
            if (TryTagForKeyword(k, out var t)) serverCustom.Add(t);

        var localCustom = new HashSet<Tag>();
        foreach (var t in input.LocalTags)
            if (SystemFlagFor(t) is null) localCustom.Add(t);

        var addKeywords = new List<string>();
        var removeKeywords = new List<string>();
        HashSet<Tag> resolvedCustom;

        if (!input.LocalTagsKnown)
        {
            resolvedCustom = serverCustom;
        }
        else if (!input.ServerAcceptsCustomKeywords)
        {
            // Keywords do not persist here, so the delta must stay keyword-free or every sync re-pushes them.
            resolvedCustom = localCustom;
        }
        else
        {
            resolvedCustom = localCustom;
            foreach (var t in localCustom)
                if (!IsLocalOnly(t) && !serverCustom.Contains(t)) addKeywords.Add(ToKeyword(t));
            foreach (var t in serverCustom)
                if (!IsLocalOnly(t) && !localCustom.Contains(t)) removeKeywords.Add(ToKeyword(t));
        }

        addKeywords.Sort(StringComparer.Ordinal);
        removeKeywords.Sort(StringComparer.Ordinal);

        var all = new HashSet<Tag>(resolvedCustom);
        foreach (var t in SystemTagsOf(flags)) all.Add(t);

        return new TagMergeResult
        {
            Flags = flags,
            Tags = Sorted(all),
            CustomTags = Sorted(resolvedCustom),
            Push = new FlagDelta { AddKeywords = addKeywords, RemoveKeywords = removeKeywords },
            FlagsChanged = flags != input.LocalFlags,
            TagsChanged = !resolvedCustom.SetEquals(localCustom),
        };
    }

    /// <summary>Convenience overload over the four merge inputs of the flag-conflict rule.</summary>
    public static TagMergeResult Merge(
        IReadOnlyList<Tag> localTags,
        MessageFlags localFlags,
        MessageFlags serverFlags,
        IReadOnlyList<string> serverKeywords,
        bool serverAcceptsCustomKeywords = true)
    {
        ArgumentNullException.ThrowIfNull(localTags);
        ArgumentNullException.ThrowIfNull(serverKeywords);

        return Merge(new TagMergeInput
        {
            LocalTags = localTags,
            LocalFlags = localFlags,
            ServerFlags = serverFlags,
            ServerKeywords = serverKeywords,
            ServerAcceptsCustomKeywords = serverAcceptsCustomKeywords,
        });
    }

    private static IReadOnlyList<Tag> Sorted(HashSet<Tag> tags)
    {
        var list = new List<Tag>(tags);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        return list;
    }
}

/// <summary>The four inputs to tag/flag conflict resolution, plus the two facts that gate it.</summary>
public sealed record TagMergeInput
{
    public IReadOnlyList<Tag> LocalTags { get; init; } = [];
    public MessageFlags LocalFlags { get; init; } = MessageFlags.None;
    public MessageFlags ServerFlags { get; init; } = MessageFlags.None;
    public IReadOnlyList<string> ServerKeywords { get; init; } = [];

    /// <summary>False when PERMANENTFLAGS omitted <c>\*</c>, which makes custom tags local-only.</summary>
    public bool ServerAcceptsCustomKeywords { get; init; } = true;

    /// <summary>False on first observation, where empty LocalTags is ignorance rather than intent.</summary>
    public bool LocalTagsKnown { get; init; } = true;
}

/// <summary>The resolved state after a merge, plus what has to go back to the server.</summary>
public sealed record TagMergeResult
{
    /// <summary>Resolved system flags. Always the server's.</summary>
    public required MessageFlags Flags { get; init; }

    /// <summary>Full tag view: system tags derived from <see cref="Flags"/> plus custom tags.</summary>
    public required IReadOnlyList<Tag> Tags { get; init; }

    /// <summary>Only the custom tags — what belongs in the <c>tags</c> table, never duplicating the bitfield.</summary>
    public required IReadOnlyList<Tag> CustomTags { get; init; }

    /// <summary>Keyword changes that make the server converge on the local custom tags. Usually empty.</summary>
    public required FlagDelta Push { get; init; }

    public bool FlagsChanged { get; init; }
    public bool TagsChanged { get; init; }
}
