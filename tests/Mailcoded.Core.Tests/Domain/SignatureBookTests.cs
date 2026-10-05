using Mailcoded.Core.Domain.Identities;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class SignatureBookTests
{
    private static readonly SignatureBook Book = SignatureBook.Empty
        .Set("work", "Alex Example\nExample Pty")
        .Set("Home", "Alex");

    [Fact]
    public void Names_are_case_insensitive_and_stored_lower_case()
    {
        Assert.Equal(["work", "home"], Book.Items.Select(i => i.Name));
        Assert.Equal("Alex", Book.Find("HOME")?.Text);
        Assert.Null(Book.Find("missing"));
        Assert.Null(Book.Find(null));
        Assert.Null(Book.Find("bad name"));
    }

    [Fact]
    public void Setting_an_existing_name_replaces_it_in_place()
    {
        var changed = Book.Set("WORK", "Replaced");

        Assert.Equal(2, changed.Items.Count);
        Assert.Equal("Replaced", changed.Items[0].Text);
    }

    [Fact]
    public void A_bad_name_or_text_is_an_argument_error()
    {
        Assert.Throws<ArgumentException>(() => Book.Set("two words", "Alex"));
        Assert.Throws<ArgumentException>(() => Book.Set("ok", ""));
        Assert.Throws<ArgumentException>(() => Book.Set("ok", new string('x', SignatureText.MaxChars + 1)));
        Assert.Equal(2, Book.Items.Count);
    }

    [Fact]
    public void The_book_is_capped_but_replacing_at_the_cap_still_works()
    {
        var full = SignatureBook.Empty;
        for (var i = 0; i < SignatureBook.MaxSignatures; i++) full = full.Set($"s{i}", "text");

        Assert.Throws<ArgumentException>(() => full.Set("one-more", "text"));
        Assert.Equal(SignatureBook.MaxSignatures, full.Set("S0", "new text").Items.Count);
    }

    [Fact]
    public void A_default_must_name_an_existing_signature_and_null_clears_it()
    {
        var withDefaults = Book.WithDefault(SignatureUse.New, "WORK").WithDefault(SignatureUse.Reply, "home");

        Assert.Equal("work", withDefaults.DefaultForNew);
        Assert.Equal("home", withDefaults.DefaultForReply);
        Assert.Throws<ArgumentException>(() => Book.WithDefault(SignatureUse.New, "missing"));
        Assert.Null(withDefaults.WithDefault(SignatureUse.New, null).DefaultForNew);
        Assert.Equal("home", withDefaults.WithDefault(SignatureUse.New, null).DefaultForReply);
    }

    [Fact]
    public void Forgetting_a_signature_clears_any_default_that_named_it()
    {
        var withDefaults = Book.WithDefault(SignatureUse.New, "work").WithDefault(SignatureUse.Reply, "work");

        var forgotten = withDefaults.Forget("Work", out var removed);

        Assert.True(removed);
        Assert.Equal(["home"], forgotten.Items.Select(i => i.Name));
        Assert.Null(forgotten.DefaultForNew);
        Assert.Null(forgotten.DefaultForReply);

        Assert.Same(forgotten, forgotten.Forget("work", out var again));
        Assert.False(again);
    }

    [Fact]
    public void Selecting_none_gives_nothing_and_selecting_a_default_follows_new_versus_reply()
    {
        var book = Book.WithDefault(SignatureUse.New, "work").WithDefault(SignatureUse.Reply, "home");

        Assert.Null(book.Select(SignatureChoice.None, SignatureUse.New));
        Assert.Null(book.Select(default, SignatureUse.Reply));
        Assert.Equal("work", book.Select(SignatureChoice.UseDefault, SignatureUse.New)?.Name);
        Assert.Equal("home", book.Select(SignatureChoice.UseDefault, SignatureUse.Reply)?.Name);
    }

    [Fact]
    public void An_unset_default_selects_nothing_but_an_unknown_name_is_an_error()
    {
        Assert.Null(Book.Select(SignatureChoice.UseDefault, SignatureUse.New));
        Assert.Null(SignatureBook.Empty.Select(SignatureChoice.UseDefault, SignatureUse.Reply));
        Assert.Equal("home", Book.Select(SignatureChoice.Named("HOME"), SignatureUse.New)?.Name);
        Assert.Throws<ArgumentException>(() => Book.Select(SignatureChoice.Named("missing"), SignatureUse.New));
        Assert.Throws<ArgumentException>(() => Book.Select(SignatureChoice.Named("bad name\r\n"), SignatureUse.New));
        Assert.Throws<ArgumentException>(() => SignatureBook.Empty.Select(SignatureChoice.Named("work"), SignatureUse.New));
    }

    [Fact]
    public void The_default_choice_asks_for_no_signature_so_existing_callers_are_unchanged()
    {
        Assert.Equal(SignatureChoiceKind.None, default(SignatureChoice).Kind);
        Assert.Equal(SignatureChoice.None, default);
    }
}
