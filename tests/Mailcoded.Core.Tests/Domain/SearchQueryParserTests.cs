using System.Diagnostics;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>Every operator, negation, quoting, script routing, escaping — and garbage that must not throw.</summary>
public sealed class SearchQueryParserTests
{
    [Fact]
    public void Address_fields_are_normalized_to_a_bare_lower_cased_address()
    {
        Assert.Equal("alice@example.com", Only<SearchPredicate.From>("from:Alice@Example.COM").Value);
        Assert.Equal("alice@example.com", Only<SearchPredicate.From>("from:\"Alice B <Alice@Example.COM>\"").Value);
        Assert.Equal("bob@example.com", Only<SearchPredicate.To>("to:Bob@Example.com").Value);
        Assert.Equal("carol@example.com", Only<SearchPredicate.Cc>("cc:Carol@Example.com").Value);
    }

    [Fact]
    public void Subject_keeps_its_case_and_collapses_its_whitespace()
    {
        Assert.Equal("Quarterly Report", Only<SearchPredicate.Subject>("subject:\"Quarterly   Report\"").Value);
    }

    [Fact]
    public void Tag_and_folder_operators_produce_their_predicates()
    {
        Assert.Equal(Tag.Parse("work"), Only<SearchPredicate.HasTag>("tag:Work").Value);
        Assert.Equal("INBOX/Work", Only<SearchPredicate.InFolder>("folder:INBOX/Work").Folder);
        Assert.Equal("Sent", Only<SearchPredicate.InFolder>("in:Sent").Folder);
    }

    [Fact]
    public void An_unparseable_tag_becomes_an_error_not_a_predicate()
    {
        var result = SearchQueryParser.Parse("tag:bad*tag");

        Assert.Empty(result.Query.Predicates);
        Assert.Contains(result.Errors, e => e.Kind == SearchParseErrorKind.InvalidTag);
    }

    [Theory]
    [InlineData("is:unread", false)]
    [InlineData("is:read", true)]
    [InlineData("is:seen", true)]
    [InlineData("-is:unread", true)]
    [InlineData("-is:read", false)]
    public void The_unread_predicate_carries_the_inversion_rather_than_a_second_predicate(string query, bool negated)
    {
        var predicate = Only<SearchPredicate.IsUnread>(query);

        Assert.Equal(negated, predicate.Negated);
    }

    [Fact]
    public void The_remaining_state_operators_parse()
    {
        Assert.False(Only<SearchPredicate.IsFlagged>("is:flagged").Negated);
        Assert.False(Only<SearchPredicate.IsFlagged>("is:starred").Negated);
        Assert.True(Only<SearchPredicate.IsFlagged>("is:unflagged").Negated);
        Assert.False(Only<SearchPredicate.IsDraft>("is:draft").Negated);
        Assert.False(Only<SearchPredicate.IsReplied>("is:replied").Negated);
        Assert.False(Only<SearchPredicate.IsReplied>("is:answered").Negated);
        Assert.False(Only<SearchPredicate.HasAttachment>("has:attachment").Negated);
        Assert.False(Only<SearchPredicate.HasAttachment>("has:attachments").Negated);
    }

    [Theory]
    [InlineData("is:nonsense", SearchParseErrorKind.UnknownValue)]
    [InlineData("has:beer", SearchParseErrorKind.UnknownValue)]
    [InlineData("tag:", SearchParseErrorKind.EmptyValue)]
    [InlineData("is:", SearchParseErrorKind.EmptyValue)]
    [InlineData("before:yesterday", SearchParseErrorKind.InvalidDate)]
    [InlineData("hello or world", SearchParseErrorKind.UnsupportedOperator)]
    public void A_bad_operator_value_reports_an_error_and_drops_the_predicate(string query, SearchParseErrorKind kind)
    {
        var result = SearchQueryParser.Parse(query);

        Assert.Contains(result.Errors, e => e.Kind == kind);
        Assert.DoesNotContain(result.Query.Predicates, p => p is SearchPredicate.HasTag or SearchPredicate.IsUnread);
    }

    [Theory]
    [InlineData("before:2026-01-31", 2026, 1, 31)]
    [InlineData("before:2026-1-5", 2026, 1, 5)]
    [InlineData("before:2026/01/31", 2026, 1, 31)]
    [InlineData("before:2026-02", 2026, 2, 1)]
    [InlineData("before:2026", 2026, 1, 1)]
    public void Date_operators_accept_iso_shapes_and_mean_utc_midnight(string query, int year, int month, int day)
    {
        var predicate = Only<SearchPredicate.BeforeDate>(query);

        Assert.Equal(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero), predicate.DateUtc);
    }

    [Fact]
    public void After_and_since_are_the_same_operator()
    {
        Assert.Equal(
            Only<SearchPredicate.AfterDate>("after:2026-01-31").DateUtc,
            Only<SearchPredicate.AfterDate>("since:2026-01-31").DateUtc);
    }

    [Fact]
    public void Search_dates_parse_the_documented_formats_and_reject_the_rest()
    {
        Assert.True(SearchQueryParser.TryParseSearchDate("2026-01-31T09:30:00Z", out var moment));
        Assert.Equal(TimeSpan.Zero, moment.Offset);

        Assert.False(SearchQueryParser.TryParseSearchDate(null, out _));
        Assert.False(SearchQueryParser.TryParseSearchDate("   ", out _));
        Assert.False(SearchQueryParser.TryParseSearchDate("last tuesday", out _));
    }

    [Fact]
    public void A_leading_dash_negates_the_next_term_or_predicate()
    {
        Assert.True(Only<SearchPredicate.HasTag>("-tag:work").Negated);
        Assert.True(SearchQueryParser.Parse("-hello").Query.Terms[0].Negated);
    }

    [Fact]
    public void The_word_not_negates_and_two_negations_cancel()
    {
        Assert.True(Only<SearchPredicate.HasTag>("NOT tag:work").Negated);
        Assert.False(Only<SearchPredicate.HasTag>("-not tag:work").Negated);
        Assert.False(Only<SearchPredicate.HasTag>("not -tag:work").Negated);
    }

    [Fact]
    public void And_is_implicit_and_or_is_rejected_without_losing_the_terms()
    {
        var and = SearchQueryParser.Parse("alpha and beta");
        var or = SearchQueryParser.Parse("alpha or beta");

        Assert.Equal(2, and.Query.Terms.Count);
        Assert.False(and.HasErrors);

        Assert.Equal(2, or.Query.Terms.Count);
        Assert.True(
            or.HasErrors,
            "OR is not supported, so it must be reported rather than silently treated as a search term — otherwise "
            + "the user gets results for the literal word 'or' and never learns why.");
    }

    [Fact]
    public void A_quoted_phrase_stays_one_term()
    {
        var query = SearchQueryParser.Parse("\"hello world\"").Query;

        var term = Assert.Single(query.Terms);
        Assert.Equal("hello world", term.Text);
        Assert.True(term.IsPhrase);
        Assert.Equal("\"hello world\"", term.MatchExpression);
    }

    [Fact]
    public void An_unterminated_quote_is_reported_and_the_rest_read_as_one_phrase()
    {
        var result = SearchQueryParser.Parse("\"hello world");

        Assert.Contains(result.Errors, e => e.Kind == SearchParseErrorKind.UnterminatedQuote);
        Assert.Equal("hello world", Assert.Single(result.Query.Terms).Text);
    }

    [Fact]
    public void A_trailing_star_becomes_an_fts5_prefix_query()
    {
        var term = Assert.Single(SearchQueryParser.Parse("hel*").Query.Terms);

        Assert.True(term.IsPrefix);
        Assert.Equal("hel", term.Text);
        Assert.Equal("\"hel\"*", term.MatchExpression);
    }

    [Fact]
    public void A_star_with_nothing_to_prefix_is_dropped_rather_than_becoming_a_syntax_error()
    {
        Assert.Empty(SearchQueryParser.Parse("*").Query.Terms);
        Assert.Empty(SearchQueryParser.Parse("***").Query.Terms);
        Assert.Empty(SearchQueryParser.Parse("!!! ??? ...").Query.Terms);
    }

    [Theory]
    [InlineData("foo\"bar")]
    [InlineData("NEAR(a")]
    [InlineData("^caret")]
    [InlineData("a:b")]
    [InlineData("(paren)")]
    [InlineData("a-b")]
    public void Every_fts5_metacharacter_is_neutralized_by_quoting(string word)
    {
        var terms = SearchQueryParser.Parse(word).Query.Terms;

        foreach (var term in terms)
        {
            var expression = term.IsPrefix ? term.MatchExpression[..^1] : term.MatchExpression;

            Assert.True(
                expression.StartsWith('"') && expression.EndsWith('"'),
                $"'{term.Text}' produced the MATCH expression {term.MatchExpression}. Every term must reach FTS5 as "
                + "a quoted string: an unquoted metacharacter turns a user's search into an FTS5 syntax error, and a "
                + "query string is untrusted input.");
        }
    }

    [Fact]
    public void An_embedded_quote_is_doubled_not_stripped()
    {
        Assert.Equal("\"foo\"\"bar\"", SearchEscaping.ToFtsString("foo\"bar"));
        Assert.Equal("\"a b\"", SearchEscaping.ToFtsString("a\u0001b"));
    }

    [Fact]
    public void Like_patterns_escape_the_wildcard_characters()
    {
        Assert.Equal("%50\\%\\_x\\\\%", SearchEscaping.ToLikePattern("50%_x\\"));
        Assert.Equal("%ab%", SearchEscaping.ToLikePattern("a\u0001b"));
    }

    [Theory]
    [InlineData("hello", ScriptKind.Latin)]
    [InlineData("café", ScriptKind.Latin)]
    [InlineData("", ScriptKind.Latin)]
    [InlineData("日本語", ScriptKind.Cjk)]
    [InlineData("こんにちは", ScriptKind.Cjk)]
    [InlineData("한국어", ScriptKind.Cjk)]
    [InlineData("日本", ScriptKind.ShortCjk)]
    [InlineData("東", ScriptKind.ShortCjk)]
    [InlineData("abc日本", ScriptKind.ShortCjk)]
    [InlineData("日本語abc", ScriptKind.Cjk)]
    public void Script_classification_routes_a_term_to_the_right_index(string term, ScriptKind expected)
    {
        Assert.Equal(expected, SearchEscaping.Classify(term));
    }

    [Fact]
    public void Latin_and_cjk_terms_go_to_their_own_match_expressions()
    {
        var query = SearchQueryParser.Parse("hello 日本語").Query;

        Assert.NotNull(query.LatinMatchExpression);
        Assert.NotNull(query.CjkMatchExpression);
        Assert.Empty(query.LikePatterns);
    }

    [Fact]
    public void A_short_cjk_term_falls_back_to_like_because_trigram_cannot_index_it()
    {
        var query = SearchQueryParser.Parse("日本").Query;

        Assert.Null(query.LatinMatchExpression);
        Assert.Null(query.CjkMatchExpression);
        Assert.Equal("%日本%", Assert.Single(query.LikePatterns));
        Assert.True(query.HasTextSearch);
    }

    [Fact]
    public void Negated_terms_are_collected_separately_so_the_store_can_subtract_them()
    {
        var query = SearchQueryParser.Parse("-hello -日本語 -日本").Query;

        Assert.Null(query.LatinMatchExpression);
        Assert.Null(query.CjkMatchExpression);
        Assert.Empty(query.LikePatterns);
        Assert.NotNull(query.NegatedLatinMatchExpression);
        Assert.NotNull(query.NegatedCjkMatchExpression);
        Assert.Single(query.NegatedLikePatterns);
    }

    [Fact]
    public void Positive_terms_are_anded_and_negated_terms_are_ored()
    {
        var positive = SearchQueryParser.Parse("alpha beta").Query;
        var negative = SearchQueryParser.Parse("-alpha -beta").Query;

        Assert.Equal("\"alpha\" AND \"beta\"", positive.LatinMatchExpression);
        Assert.Equal("\"alpha\" OR \"beta\"", negative.NegatedLatinMatchExpression);
    }

    [Fact]
    public void An_empty_query_is_empty()
    {
        foreach (var raw in new string?[] { null, "", "   ", "\t\n" })
        {
            var result = SearchQueryParser.Parse(raw);

            Assert.True(result.Query.IsEmpty);
            Assert.False(result.Query.HasTextSearch);
            Assert.False(result.HasErrors);
        }
    }

    [Fact]
    public void The_term_budget_is_enforced_and_reported()
    {
        var words = string.Join(' ', Enumerable.Range(0, 70).Select(i => "w" + i));

        var result = SearchQueryParser.Parse(words);

        Assert.Equal(SearchQueryParser.MaxTerms, result.Query.Terms.Count);
        Assert.Contains(result.Errors, e => e.Kind == SearchParseErrorKind.TooManyTerms);
    }

    [Fact]
    public void An_over_long_query_is_truncated_and_reported()
    {
        var result = SearchQueryParser.Parse(new string('a', SearchQueryParser.MaxQueryLength + 500));

        Assert.Contains(result.Errors, e => e.Kind == SearchParseErrorKind.TooLong);
        Assert.Equal(SearchQueryParser.MaxQueryLength, result.Query.RawQuery.Length);
        Assert.Equal(SearchEscaping.MaxTermLength, Assert.Single(result.Query.Terms).Text.Length);
    }

    [Theory]
    [InlineData(":")]
    [InlineData("::::")]
    [InlineData(":::: ::::")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("-:")]
    [InlineData("\"")]
    [InlineData("\"\"\"\"\"")]
    [InlineData("from:")]
    [InlineData("from::")]
    [InlineData("\\")]
    [InlineData("%_%")]
    [InlineData("NEAR(a b, 2)")]
    [InlineData("a AND OR NOT b")]
    [InlineData("tag:tag:tag:tag")]
    [InlineData("\u0000\u0001\u0002")]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE messages; --")]
    [InlineData("日")]
    [InlineData("😀")]
    public void Garbage_input_never_throws(string query)
    {
        var result = SearchQueryParser.Parse(query);

        Assert.NotNull(result.Query);
        Assert.NotNull(result.Errors);
    }

    [Fact]
    public void Randomly_generated_garbage_never_throws_and_always_terminates()
    {
        var random = new Random(12345);
        var alphabet = "abc \"'-:*()[]{}^%_\\/,;.@<>|&!?0123456789\t\n日本語".ToCharArray();

        var elapsed = Stopwatch.StartNew();

        for (var i = 0; i < 3_000; i++)
        {
            var length = random.Next(0, 64);
            var chars = new char[length];
            for (var c = 0; c < length; c++) chars[c] = alphabet[random.Next(alphabet.Length)];

            var result = SearchQueryParser.Parse(new string(chars));

            Assert.NotNull(result.Query);
            Assert.True(result.Query.Terms.Count <= SearchQueryParser.MaxTerms);
        }

        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(20),
            $"3000 garbage queries took {elapsed.Elapsed}. A search string is untrusted input typed by a user or "
            + "an agent: the parser must be linear and must never be able to hang the daemon.");
    }

    [Fact]
    public void The_parser_normalizes_untrusted_text_before_it_reaches_a_term()
    {
        Assert.Equal("a b", SearchEscaping.NormalizeTerm("  a \t\r\n b  "));
        Assert.Equal(string.Empty, SearchEscaping.NormalizeTerm(null));
        Assert.Equal(SearchEscaping.MaxTermLength, SearchEscaping.NormalizeTerm(new string('a', 500)).Length);

        Assert.False(SearchEscaping.HasTokenChar(null));
        Assert.False(SearchEscaping.HasTokenChar(""));
        Assert.False(SearchEscaping.HasTokenChar("!!!"));
        Assert.True(SearchEscaping.HasTokenChar("a"));
        Assert.True(SearchEscaping.HasTokenChar("日"));
    }

    private static T Only<T>(string query) where T : SearchPredicate
    {
        var parsed = SearchQueryParser.Parse(query).Query;
        return Assert.IsType<T>(Assert.Single(parsed.Predicates));
    }
}
