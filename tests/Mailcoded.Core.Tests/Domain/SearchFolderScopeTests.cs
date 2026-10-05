using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class SearchFolderScopeTests
{
    [Theory]
    [InlineData("", false, true)]
    [InlineData("invoice", false, true)]
    [InlineData("tag:unread from:acme", false, true)]
    [InlineData("invoice", true, false)]
    [InlineData("in:inbox", false, false)]
    [InlineData("folder:INBOX/Work invoice", false, false)]
    [InlineData("in:trash", false, false)]
    [InlineData("folder:junk", false, false)]
    [InlineData("in:Spam", false, false)]
    [InlineData("in:anywhere", false, false)]
    [InlineData("folder:anywhere invoice", false, false)]
    [InlineData("invoice in:ANYWHERE", false, false)]
    [InlineData("in:\"anywhere\"", false, false)]
    [InlineData("-in:trash", false, true)]
    [InlineData("-in:inbox invoice", false, true)]
    [InlineData("-folder:junk", false, true)]
    [InlineData("-in:trash in:inbox", false, false)]
    [InlineData("not in:inbox", false, true)]
    [InlineData("anywhere", false, true)]
    [InlineData("subject:in:anywhere", false, true)]
    [InlineData("\"in:anywhere\"", false, true)]
    public void Default_scope_hides_Trash_and_Junk_unless_the_search_names_where_to_look(
        string query,
        bool scopedToFolder,
        bool expectedHidden)
    {
        var parsed = SearchQueryParser.Parse(query).Query;

        Assert.Equal(expectedHidden, SearchFolderScope.HidesByDefault(parsed, scopedToFolder));
    }

    [Fact]
    public void A_Folder_scope_on_the_request_lifts_the_default_even_for_a_query_that_names_nothing()
    {
        Assert.False(SearchFolderScope.HidesByDefault(SearchQuery.Empty, scopedToFolder: true));
        Assert.True(SearchFolderScope.HidesByDefault(SearchQuery.Empty, scopedToFolder: false));
    }

    [Fact]
    public void Exactly_Trash_and_Junk_are_hidden()
    {
        Assert.Equal([FolderRole.Trash, FolderRole.Junk], SearchFolderScope.HiddenByDefault);
    }

    [Theory]
    [InlineData("in:anywhere")]
    [InlineData("folder:anywhere")]
    [InlineData("IN:Anywhere")]
    [InlineData("in:\"ANYWHERE\"")]
    public void Anywhere_is_a_keyword_not_a_Folder_name(string query)
    {
        var result = SearchQueryParser.Parse(query);

        Assert.IsType<SearchPredicate.InAnyFolder>(Assert.Single(result.Query.Predicates));
        Assert.True(result.Query.NamesFolderScope);
    }

    [Fact]
    public void Anywhere_keeps_the_rest_of_the_query_intact()
    {
        var result = SearchQueryParser.Parse("invoice in:anywhere from:acme");

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Query.Predicates.Count);
        Assert.Contains(result.Query.Predicates, p => p is SearchPredicate.InAnyFolder);
        Assert.Contains(result.Query.Predicates, p => p is SearchPredicate.From);
        Assert.Equal("invoice", Assert.Single(result.Query.Terms).Text);
    }

    [Theory]
    [InlineData("-in:anywhere")]
    [InlineData("not in:anywhere")]
    [InlineData("-folder:anywhere")]
    public void A_negated_anywhere_is_an_error_and_names_no_scope(string query)
    {
        var result = SearchQueryParser.Parse(query);

        Assert.Empty(result.Query.Predicates);
        Assert.False(result.Query.NamesFolderScope);
        Assert.Equal(SearchParseErrorKind.UnknownValue, Assert.Single(result.Errors).Kind);
        Assert.True(SearchFolderScope.HidesByDefault(result.Query, scopedToFolder: false));
    }

    [Theory]
    [InlineData("in:anywhere/Work", "anywhere/Work")]
    [InlineData("in:anywhere2", "anywhere2")]
    [InlineData("in:anywhere-else", "anywhere-else")]
    [InlineData("in:\"any where\"", "any where")]
    [InlineData("folder:Anywhere/", "Anywhere/")]
    public void Only_the_bare_word_is_the_keyword_anything_longer_is_a_Folder_name(string query, string folder)
    {
        var predicate = Assert.IsType<SearchPredicate.InFolder>(Assert.Single(SearchQueryParser.Parse(query).Query.Predicates));

        Assert.Equal(folder, predicate.Folder);
    }

    [Theory]
    [InlineData("in:\u0000anywhere")]
    [InlineData("in:anywhere\u0000")]
    public void Control_characters_around_the_value_are_dropped_before_it_is_compared(string query)
    {
        Assert.IsType<SearchPredicate.InAnyFolder>(Assert.Single(SearchQueryParser.Parse(query).Query.Predicates));
    }

    [Theory]
    [InlineData("in:any\u200Bwhere")]
    [InlineData("in:anywhere\u200B")]
    [InlineData("in:\u0430nywhere")]
    [InlineData("in:anywhere%")]
    [InlineData("in:anywhere*")]
    [InlineData("in:anywhere' OR 1=1 --")]
    public void Look_alikes_and_injection_shaped_values_are_ordinary_Folder_names(string query)
    {
        var result = SearchQueryParser.Parse(query);

        Assert.All(result.Query.Predicates, p => Assert.IsType<SearchPredicate.InFolder>(p));
        Assert.DoesNotContain(result.Query.Predicates, p => p is SearchPredicate.InAnyFolder);
    }

    [Theory]
    [InlineData("in:")]
    [InlineData("in:\"\"")]
    [InlineData("in:   ")]
    public void An_empty_value_is_an_error_and_names_no_scope(string query)
    {
        var result = SearchQueryParser.Parse(query);

        Assert.Empty(result.Query.Predicates);
        Assert.Contains(result.Errors, e => e.Kind == SearchParseErrorKind.EmptyValue);
        Assert.True(SearchFolderScope.HidesByDefault(result.Query, scopedToFolder: false));
    }

    [Fact]
    public void An_unterminated_quote_around_anywhere_still_reads_as_the_keyword_and_reports_the_quote()
    {
        var result = SearchQueryParser.Parse("in:\"anywhere");

        Assert.IsType<SearchPredicate.InAnyFolder>(Assert.Single(result.Query.Predicates));
        Assert.Contains(result.Errors, e => e.Kind == SearchParseErrorKind.UnterminatedQuote);
    }

    [Fact]
    public void A_very_long_value_is_cut_and_is_not_the_keyword()
    {
        var result = SearchQueryParser.Parse("in:" + new string('a', 5000));

        Assert.All(result.Query.Predicates, p => Assert.IsNotType<SearchPredicate.InAnyFolder>(p));
    }
}
