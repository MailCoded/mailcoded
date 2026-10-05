namespace Mailcoded.Core.Domain.Correspondents;

/// <summary>Frequency and recency, then a prefix filter: the order a compose box should offer people in.</summary>
public static class CorrespondentRanking
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 100;
    public const int MaxPrefixLength = 128;

    private const double HalfLifeDays = 90;
    private const double RecencyFloor = 0.25;

    /// <summary>Each use adds less than the last (log), and recency decays toward a floor so a long-standing contact outlives one stray recent name.</summary>
    public static double Score(int count, DateTimeOffset lastUsedUtc, DateTimeOffset nowUtc)
    {
        var ageDays = Math.Max(0, (nowUtc - lastUsedUtc).TotalDays);
        var recency = RecencyFloor + (1 - RecencyFloor) * Math.Pow(2, -ageDays / HalfLifeDays);
        return (1 + Math.Log(Math.Max(1, count))) * recency;
    }

    /// <summary>An exact address match first, then best score; ties fall to count, then recency, then the address, so the order never wobbles.</summary>
    public static IReadOnlyList<Correspondent> Suggest(
        IEnumerable<Correspondent> all,
        string? prefix,
        int limit,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(all);

        var needle = NormalizePrefix(prefix);
        var ranked = new List<(Correspondent Row, double Score, bool Exact)>();

        foreach (var row in all)
        {
            if (!Matches(row, needle)) continue;

            var exact = needle.Length > 0 && string.Equals(row.Address, needle, StringComparison.OrdinalIgnoreCase);
            ranked.Add((row, Score(row.Count, row.LastUsedUtc, nowUtc), exact));
        }

        ranked.Sort(static (a, b) =>
        {
            var byExact = b.Exact.CompareTo(a.Exact);
            if (byExact != 0) return byExact;

            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;

            var byCount = b.Row.Count.CompareTo(a.Row.Count);
            if (byCount != 0) return byCount;

            var byRecency = b.Row.LastUsedUtc.CompareTo(a.Row.LastUsedUtc);
            return byRecency != 0 ? byRecency : string.CompareOrdinal(a.Row.Address, b.Row.Address);
        });

        var take = Math.Min(Math.Max(limit, 0), ranked.Count);
        var result = new List<Correspondent>(take);
        for (var i = 0; i < take; i++) result.Add(ranked[i].Row);
        return result;
    }

    /// <summary>The address, the whole display name, or any word of it, starts with the prefix; an empty prefix matches everyone.</summary>
    public static bool Matches(Correspondent row, string normalizedPrefix)
    {
        if (normalizedPrefix.Length == 0) return true;

        if (row.Address.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (row.DisplayName is not { Length: > 0 } name) return false;
        if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) return true;

        var wordStart = -1;
        for (var i = 0; i <= name.Length; i++)
        {
            var inWord = i < name.Length && char.IsLetterOrDigit(name[i]);
            if (inWord)
            {
                if (wordStart < 0) wordStart = i;
                continue;
            }

            if (wordStart >= 0 && name.AsSpan(wordStart).StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            wordStart = -1;
        }

        return false;
    }

    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;

        var trimmed = prefix.Trim();
        return trimmed.Length > MaxPrefixLength ? trimmed[..MaxPrefixLength] : trimmed;
    }
}
