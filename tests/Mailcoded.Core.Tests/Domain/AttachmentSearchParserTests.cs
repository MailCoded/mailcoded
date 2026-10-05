using Mailcoded.Core.Domain.Search;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class AttachmentSearchParserTests
{
    private static SearchPredicate.AttachmentText Only(string query)
    {
        var result = SearchQueryParser.Parse(query);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return Assert.IsType<SearchPredicate.AttachmentText>(Assert.Single(result.Query.Predicates));
    }

    [Fact]
    public void A_word_becomes_an_attachment_predicate_not_a_body_term()
    {
        var result = SearchQueryParser.Parse("attachment:wiring");

        var predicate = Assert.IsType<SearchPredicate.AttachmentText>(Assert.Single(result.Query.Predicates));
        Assert.Equal("wiring", predicate.Term.Text);
        Assert.Equal("\"wiring\"", predicate.Term.MatchExpression);
        Assert.Equal(ScriptKind.Latin, predicate.Term.Script);
        Assert.Empty(result.Query.Terms);
        Assert.Null(result.Query.LatinMatchExpression);
        Assert.False(result.Query.HasTextSearch);
    }

    [Fact]
    public void The_operator_name_is_case_insensitive_and_can_be_negated()
    {
        Assert.False(Only("ATTACHMENT:wiring").Negated);
        Assert.True(Only("-attachment:wiring").Negated);
        Assert.True(Only("NOT attachment:wiring").Negated);
    }

    [Fact]
    public void A_quoted_value_is_a_phrase()
    {
        var predicate = Only("attachment:\"purchase order\"");

        Assert.True(predicate.Term.IsPhrase);
        Assert.Equal("\"purchase order\"", predicate.Term.MatchExpression);
    }

    [Fact]
    public void A_trailing_star_is_a_prefix_and_a_quoted_one_is_not()
    {
        Assert.True(Only("attachment:wir*").Term.IsPrefix);
        Assert.Equal("\"wir\"*", Only("attachment:wir*").Term.MatchExpression);
        Assert.False(Only("attachment:\"wir*\"").Term.IsPrefix);
    }

    [Theory]
    [InlineData("attachment:项目进度", ScriptKind.Cjk)]
    [InlineData("attachment:进度", ScriptKind.ShortCjk)]
    public void Cjk_values_are_routed_like_body_terms(string query, ScriptKind script)
    {
        var predicate = Only(query);

        Assert.Equal(script, predicate.Term.Script);
        Assert.Equal(script == ScriptKind.ShortCjk, predicate.Term.LikePattern is not null);
    }

    [Fact]
    public void The_primary_term_is_the_first_positive_one()
    {
        var query = SearchQueryParser.Parse("-attachment:skip attachment:first attachment:second").Query;

        Assert.Equal("first", query.PrimaryAttachmentTerm?.Text);
        Assert.Null(SearchQueryParser.Parse("-attachment:only").Query.PrimaryAttachmentTerm);
        Assert.Null(SearchQueryParser.Parse("wiring").Query.PrimaryAttachmentTerm);
    }

    [Theory]
    [InlineData("attachment:")]
    [InlineData("attachment:\"\"")]
    [InlineData("attachment:***")]
    [InlineData("attachment:---")]
    public void A_value_with_nothing_to_look_for_is_an_error_and_no_predicate(string query)
    {
        var result = SearchQueryParser.Parse(query);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Query.Predicates);
        Assert.Equal(SearchParseErrorKind.EmptyValue, result.Errors[0].Kind);
    }

    [Fact]
    public void Metacharacters_stay_inside_the_quoted_match_expression()
    {
        var predicate = Only("attachment:\"a\"\"b\"");
        Assert.StartsWith("\"", predicate.Term.MatchExpression, StringComparison.Ordinal);
        Assert.EndsWith("\"", predicate.Term.MatchExpression, StringComparison.Ordinal);

        var injected = SearchQueryParser.Parse("attachment:x\" OR a:b").Query;
        Assert.All(injected.Predicates.OfType<SearchPredicate.AttachmentText>(), p => Assert.StartsWith("\"", p.Term.MatchExpression, StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_operators_and_bare_words_are_unchanged_beside_it()
    {
        var query = SearchQueryParser.Parse("invoice has:attachment attachment:wiring from:alice@example.com").Query;

        Assert.Equal(3, query.Predicates.Count);
        Assert.Single(query.Terms);
        Assert.Equal("\"invoice\"", query.LatinMatchExpression);
    }
}
