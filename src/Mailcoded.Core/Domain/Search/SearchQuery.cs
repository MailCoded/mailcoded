using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Search;

/// <summary>A metadata filter. Sealed hierarchy with an exhaustive switch, like SyncPlan.</summary>
public abstract record SearchPredicate
{
    private SearchPredicate() { }

    public bool Negated { get; init; }

    public sealed record From(string Value) : SearchPredicate;
    public sealed record To(string Value) : SearchPredicate;
    public sealed record Cc(string Value) : SearchPredicate;
    public sealed record Subject(string Value) : SearchPredicate;
    public sealed record HasTag(Tag Value) : SearchPredicate;
    public sealed record InFolder(string Folder) : SearchPredicate;
    public sealed record IsUnread : SearchPredicate;
    public sealed record IsFlagged : SearchPredicate;
    public sealed record IsDraft : SearchPredicate;
    public sealed record IsReplied : SearchPredicate;
    public sealed record HasAttachment : SearchPredicate;

    /// <summary>Strictly earlier than the given instant.</summary>
    public sealed record BeforeDate(DateTimeOffset DateUtc) : SearchPredicate;

    /// <summary>At or after the given instant.</summary>
    public sealed record AfterDate(DateTimeOffset DateUtc) : SearchPredicate;
}

/// <summary>One full-text term, already escaped. <see cref="Script"/> tells the store which index to use.</summary>
public sealed record FtsTerm
{
    public required string Text { get; init; }

    /// <summary>The FTS5-safe form: a quoted string, optionally suffixed with the prefix operator.</summary>
    public required string MatchExpression { get; init; }

    public required ScriptKind Script { get; init; }
    public bool IsPhrase { get; init; }
    public bool IsPrefix { get; init; }
    public bool Negated { get; init; }

    /// <summary>Set only for short-CJK terms, which no FTS5 tokenizer here can index.</summary>
    public string? LikePattern { get; init; }
}

/// <summary>The parsed query: metadata predicates plus the residual full-text expression.</summary>
public sealed record SearchQuery
{
    public string RawQuery { get; init; } = string.Empty;
    public IReadOnlyList<SearchPredicate> Predicates { get; init; } = [];
    public IReadOnlyList<FtsTerm> Terms { get; init; } = [];

    /// <summary>MATCH expression for msg_fts, or null when no Latin term was given.</summary>
    public string? LatinMatchExpression { get; init; }

    /// <summary>MATCH expression for msg_fts_cjk, or null when no CJK term was given.</summary>
    public string? CjkMatchExpression { get; init; }

    /// <summary>Patterns for the LIKE fallback, bound with <c>ESCAPE '\'</c>.</summary>
    public IReadOnlyList<string> LikePatterns { get; init; } = [];

    public string? NegatedLatinMatchExpression { get; init; }
    public string? NegatedCjkMatchExpression { get; init; }
    public IReadOnlyList<string> NegatedLikePatterns { get; init; } = [];

    public bool IsEmpty => Predicates.Count == 0 && Terms.Count == 0;

    public bool HasTextSearch =>
        LatinMatchExpression is not null
        || CjkMatchExpression is not null
        || LikePatterns.Count > 0
        || NegatedLatinMatchExpression is not null
        || NegatedCjkMatchExpression is not null
        || NegatedLikePatterns.Count > 0;

    public static readonly SearchQuery Empty = new();
}

public enum SearchParseErrorKind
{
    TooLong,
    TooManyTerms,
    EmptyValue,
    UnknownValue,
    InvalidTag,
    InvalidDate,
    UnterminatedQuote,
    UnsupportedOperator,
}

public sealed record SearchParseError(SearchParseErrorKind Kind, string Message, int Position, string Token);

/// <summary>Parsing never throws; a malformed query yields a best-effort query plus errors.</summary>
public sealed record SearchParseResult
{
    public required SearchQuery Query { get; init; }
    public IReadOnlyList<SearchParseError> Errors { get; init; } = [];
    public bool HasErrors => Errors.Count > 0;
}
