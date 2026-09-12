using System.Globalization;
using System.Text;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Search;

/// <summary>Parses the CLI/RPC search syntax into predicates plus escaped full-text terms. Never emits SQL.</summary>
public static class SearchQueryParser
{
    public const int MaxQueryLength = 4096;
    public const int MaxTerms = 64;
    private const int MaxExcerptLength = 64;

    private enum FieldKind
    {
        From,
        To,
        Cc,
        Subject,
        Tag,
        Folder,
        Is,
        Has,
        Before,
        After,
    }

    public static SearchParseResult Parse(string? query)
    {
        var errors = new List<SearchParseError>();
        var raw = query ?? string.Empty;

        if (raw.Length > MaxQueryLength)
        {
            errors.Add(new SearchParseError(
                SearchParseErrorKind.TooLong,
                $"Query truncated to {MaxQueryLength} characters.",
                MaxQueryLength,
                string.Empty));
            raw = raw[..MaxQueryLength];
        }

        var predicates = new List<SearchPredicate>();
        var terms = new List<FtsTerm>();
        var accepted = 0;
        var pendingNegate = false;
        var i = 0;

        while (i < raw.Length)
        {
            while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
            if (i >= raw.Length) break;

            var start = i;
            if (accepted >= MaxTerms)
            {
                errors.Add(new SearchParseError(
                    SearchParseErrorKind.TooManyTerms,
                    $"Only the first {MaxTerms} terms are used.",
                    start,
                    string.Empty));
                break;
            }

            var negated = pendingNegate;
            pendingNegate = false;

            if (raw[i] == '-' && i + 1 < raw.Length && !char.IsWhiteSpace(raw[i + 1]))
            {
                negated = !negated;
                i++;
            }

            var word = ReadWord(raw, ref i, out var quoted, out var unterminated, out var colon);

            if (unterminated)
                errors.Add(new SearchParseError(
                    SearchParseErrorKind.UnterminatedQuote,
                    "Unterminated quote; the rest of the query was read as one phrase.",
                    start,
                    Excerpt(word)));

            if (word.Length == 0) continue;

            if (!quoted && colon < 0)
            {
                if (word.Equals("and", StringComparison.OrdinalIgnoreCase)) continue;

                if (word.Equals("not", StringComparison.OrdinalIgnoreCase))
                {
                    pendingNegate = !negated;
                    continue;
                }

                if (word.Equals("or", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new SearchParseError(
                        SearchParseErrorKind.UnsupportedOperator,
                        "OR is not an operator you can type; terms are combined with AND, and a "
                            + "query that matches nothing is retried with OR automatically.",
                        start,
                        Excerpt(word)));
                    continue;
                }
            }

            if (colon > 0 && TryField(word[..colon], out var field))
            {
                if (AddPredicate(field, word[(colon + 1)..], negated, start, predicates, errors)) accepted++;
                continue;
            }

            if (AddTerm(word, quoted, negated, terms)) accepted++;
        }

        return new SearchParseResult { Query = Build(raw, predicates, terms), Errors = errors };
    }

    private static SearchQuery Build(string raw, List<SearchPredicate> predicates, List<FtsTerm> terms)
    {
        var latin = new List<string>();
        var cjk = new List<string>();
        var like = new List<string>();
        var negatedLatin = new List<string>();
        var negatedCjk = new List<string>();
        var negatedLike = new List<string>();

        foreach (var t in terms)
        {
            switch (t.Script)
            {
                case ScriptKind.Latin:
                    (t.Negated ? negatedLatin : latin).Add(t.MatchExpression);
                    break;
                case ScriptKind.Cjk:
                    (t.Negated ? negatedCjk : cjk).Add(t.MatchExpression);
                    break;
                case ScriptKind.ShortCjk:
                    if (t.LikePattern is { } pattern) (t.Negated ? negatedLike : like).Add(pattern);
                    break;
            }
        }

        return new SearchQuery
        {
            RawQuery = raw,
            Predicates = predicates,
            Terms = terms,
            LatinMatchExpression = latin.Count == 0 ? null : string.Join(" AND ", latin),
            CjkMatchExpression = cjk.Count == 0 ? null : string.Join(" AND ", cjk),
            RelaxedLatinMatchExpression = latin.Count < 2 ? null : string.Join(" OR ", latin),
            RelaxedCjkMatchExpression = cjk.Count < 2 ? null : string.Join(" OR ", cjk),
            LikePatterns = like,
            NegatedLatinMatchExpression = negatedLatin.Count == 0 ? null : string.Join(" OR ", negatedLatin),
            NegatedCjkMatchExpression = negatedCjk.Count == 0 ? null : string.Join(" OR ", negatedCjk),
            NegatedLikePatterns = negatedLike,
        };
    }

    private static string ReadWord(string s, ref int i, out bool quoted, out bool unterminated, out int colon)
    {
        var sb = new StringBuilder();
        quoted = false;
        unterminated = false;
        colon = -1;

        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) break;

            if (c == '"')
            {
                quoted = true;
                i++;
                while (i < s.Length && s[i] != '"') { sb.Append(s[i]); i++; }
                if (i < s.Length) i++; else unterminated = true;
                continue;
            }

            if (c == ':' && colon < 0) colon = sb.Length;
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool TryField(string name, out FieldKind field)
    {
        switch (name.ToLowerInvariant())
        {
            case "from": field = FieldKind.From; return true;
            case "to": field = FieldKind.To; return true;
            case "cc": field = FieldKind.Cc; return true;
            case "subject": field = FieldKind.Subject; return true;
            case "tag": field = FieldKind.Tag; return true;
            case "folder":
            case "in": field = FieldKind.Folder; return true;
            case "is": field = FieldKind.Is; return true;
            case "has": field = FieldKind.Has; return true;
            case "before": field = FieldKind.Before; return true;
            case "after":
            case "since": field = FieldKind.After; return true;
            default: field = default; return false;
        }
    }

    private static bool AddPredicate(
        FieldKind field,
        string rawValue,
        bool negated,
        int position,
        List<SearchPredicate> predicates,
        List<SearchParseError> errors)
    {
        var value = SearchEscaping.NormalizeTerm(rawValue);
        if (value.Length == 0)
        {
            errors.Add(new SearchParseError(SearchParseErrorKind.EmptyValue, "This field needs a value.", position, string.Empty));
            return false;
        }

        switch (field)
        {
            case FieldKind.From:
                predicates.Add(new SearchPredicate.From(NormalizeAddress(value)) { Negated = negated });
                return true;

            case FieldKind.To:
                predicates.Add(new SearchPredicate.To(NormalizeAddress(value)) { Negated = negated });
                return true;

            case FieldKind.Cc:
                predicates.Add(new SearchPredicate.Cc(NormalizeAddress(value)) { Negated = negated });
                return true;

            case FieldKind.Subject:
                predicates.Add(new SearchPredicate.Subject(value) { Negated = negated });
                return true;

            case FieldKind.Tag:
                if (!Tag.TryParse(value, out var tag))
                {
                    errors.Add(new SearchParseError(SearchParseErrorKind.InvalidTag, "Not a valid tag.", position, Excerpt(value)));
                    return false;
                }
                predicates.Add(new SearchPredicate.HasTag(tag) { Negated = negated });
                return true;

            case FieldKind.Folder:
                predicates.Add(new SearchPredicate.InFolder(value) { Negated = negated });
                return true;

            case FieldKind.Is:
                return AddIsPredicate(value, negated, position, predicates, errors);

            case FieldKind.Has:
                if (value.StartsWith("attach", StringComparison.OrdinalIgnoreCase))
                {
                    predicates.Add(new SearchPredicate.HasAttachment { Negated = negated });
                    return true;
                }
                errors.Add(new SearchParseError(SearchParseErrorKind.UnknownValue, "Only has:attachment is supported.", position, Excerpt(value)));
                return false;

            case FieldKind.Before:
            case FieldKind.After:
                if (!TryParseSearchDate(value, out var date))
                {
                    errors.Add(new SearchParseError(SearchParseErrorKind.InvalidDate, "Expected an ISO date such as 2026-01-31.", position, Excerpt(value)));
                    return false;
                }
                if (field == FieldKind.Before)
                    predicates.Add(new SearchPredicate.BeforeDate(date) { Negated = negated });
                else
                    predicates.Add(new SearchPredicate.AfterDate(date) { Negated = negated });
                return true;

            default:
                return false;
        }
    }

    private static bool AddIsPredicate(
        string value,
        bool negated,
        int position,
        List<SearchPredicate> predicates,
        List<SearchParseError> errors)
    {
        switch (value.ToLowerInvariant())
        {
            case "unread":
                predicates.Add(new SearchPredicate.IsUnread { Negated = negated });
                return true;
            case "read":
            case "seen":
                predicates.Add(new SearchPredicate.IsUnread { Negated = !negated });
                return true;
            case "flagged":
            case "starred":
                predicates.Add(new SearchPredicate.IsFlagged { Negated = negated });
                return true;
            case "unflagged":
                predicates.Add(new SearchPredicate.IsFlagged { Negated = !negated });
                return true;
            case "draft":
                predicates.Add(new SearchPredicate.IsDraft { Negated = negated });
                return true;
            case "replied":
            case "answered":
                predicates.Add(new SearchPredicate.IsReplied { Negated = negated });
                return true;
            default:
                errors.Add(new SearchParseError(
                    SearchParseErrorKind.UnknownValue,
                    "Expected unread, read, flagged, unflagged, draft, or replied.",
                    position,
                    Excerpt(value)));
                return false;
        }
    }

    private static bool AddTerm(string word, bool quoted, bool negated, List<FtsTerm> terms)
    {
        var text = SearchEscaping.NormalizeTerm(word);
        var isPrefix = false;

        if (!quoted && text.EndsWith('*'))
        {
            var trimmed = text.TrimEnd('*');
            if (SearchEscaping.HasTokenChar(trimmed))
            {
                text = trimmed;
                isPrefix = true;
            }
        }

        // A term FTS5 cannot tokenize would turn the whole MATCH into a syntax error.
        if (!SearchEscaping.HasTokenChar(text)) return false;

        var script = SearchEscaping.Classify(text);
        var match = SearchEscaping.ToFtsString(text);
        if (isPrefix) match += "*";

        terms.Add(new FtsTerm
        {
            Text = text,
            MatchExpression = match,
            Script = script,
            IsPhrase = quoted,
            IsPrefix = isPrefix,
            Negated = negated,
            LikePattern = script == ScriptKind.ShortCjk ? SearchEscaping.ToLikePattern(text) : null,
        });

        return true;
    }

    /// <summary>Accepts yyyy-MM-dd, yyyy-MM, yyyy, and full ISO 8601. Date-only values mean UTC midnight.</summary>
    public static bool TryParseSearchDate(string? value, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim().Replace('/', '-');
        string[] dateOnly = ["yyyy-MM-dd", "yyyy-M-d", "yyyy-MM", "yyyy"];

        if (DateTime.TryParseExact(s, dateOnly, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            utc = new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc));
            return true;
        }

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment))
        {
            utc = moment.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static string NormalizeAddress(string value)
    {
        var s = value.Trim();
        var lt = s.LastIndexOf('<');
        if (lt >= 0)
        {
            var gt = s.IndexOf('>', lt + 1);
            if (gt > lt + 1) s = s[(lt + 1)..gt];
        }
        return s.Trim().ToLowerInvariant();
    }

    private static string Excerpt(string value) =>
        value.Length <= MaxExcerptLength ? value : value[..MaxExcerptLength];
}
