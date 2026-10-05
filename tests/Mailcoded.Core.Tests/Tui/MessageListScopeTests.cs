using Mailcoded.Protocol;
using Mailcoded.Tui;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

public sealed class MessageListScopeTests
{
    [Theory]
    [InlineData("inbox")]
    [InlineData("trash")]
    [InlineData("junk")]
    [InlineData(null)]
    public void Browsing_a_Folder_lists_that_Folder(string? role) =>
        Assert.Equal(7, MessageListScope.FolderFilter(Folder(role), query: null));

    [Theory]
    [InlineData("inbox")]
    [InlineData("sent")]
    [InlineData("archive")]
    [InlineData("all")]
    [InlineData(null)]
    public void A_search_from_an_ordinary_Folder_covers_the_whole_account(string? role) =>
        Assert.Null(MessageListScope.FolderFilter(Folder(role), query: "invoice"));

    [Theory]
    [InlineData("trash")]
    [InlineData("junk")]
    public void A_search_from_a_Folder_the_account_wide_search_hides_stays_in_that_Folder(string role) =>
        Assert.Equal(7, MessageListScope.FolderFilter(Folder(role), query: "invoice"));

    [Theory]
    [InlineData("trash", "invoice in:anywhere")]
    [InlineData("junk", "invoice IN:ANYWHERE")]
    [InlineData("trash", "invoice in:inbox")]
    [InlineData("junk", "invoice folder:Inbox")]
    [InlineData("trash", "in:inbox invoice")]
    [InlineData("junk", "invoice in:\"Sent Items\"")]
    [InlineData("trash", "invoice not -in:inbox")]
    [InlineData("junk", "invoice \"not\" in:inbox")]
    public void A_query_that_names_a_Folder_or_says_anywhere_is_not_pinned_to_the_selected_Folder(string role, string query) =>
        Assert.Null(MessageListScope.FolderFilter(Folder(role), query));

    [Theory]
    [InlineData("trash", "invoice -in:inbox")]
    [InlineData("junk", "invoice -IN:inbox")]
    [InlineData("trash", "invoice not in:inbox")]
    [InlineData("junk", "invoice NOT folder:Inbox")]
    [InlineData("trash", "invoice \"in:inbox\"")]
    [InlineData("junk", "invoice in:")]
    [InlineData("trash", "invoice login:inbox")]
    [InlineData("trash", "invoice --in:inbox")]
    [InlineData("junk", "\"invoice in:inbox\"")]
    [InlineData("trash", "subject:\"re in:inbox\"")]
    [InlineData("junk", "invoice in:\"\"")]
    [InlineData("trash", "invoice \"in:anywhere\"")]
    public void A_negated_or_empty_or_quoted_Folder_token_leaves_the_search_in_the_selected_Folder(string role, string query) =>
        Assert.Equal(7, MessageListScope.FolderFilter(Folder(role), query));

    private static FolderDto Folder(string? role) =>
        new() { Id = 7, AccountId = 1, Name = "Any", Role = role };
}
