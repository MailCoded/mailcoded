using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>Tag charset and FolderPath normalization, including the INBOX and Windows rules.</summary>
public sealed class TagAndFolderPathTests
{
    [Theory]
    [InlineData("work", "work")]
    [InlineData("WORK", "work")]
    [InlineData("  Work  ", "work")]
    [InlineData("a-b_c.d+e", "a-b_c.d+e")]
    [InlineData("$MDNSent", "$mdnsent")]
    [InlineData("#todo", "#todo")]
    [InlineData("!urgent", "!urgent")]
    public void A_valid_tag_is_trimmed_and_lower_cased(string raw, string expected)
    {
        Assert.True(Tag.TryParse(raw, out var tag));
        Assert.Equal(expected, tag.Value);
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("with space")]
    [InlineData("tab\u0009here")]
    [InlineData("newline\u000ahere")]
    [InlineData("nul\u0000here")]
    [InlineData("del\u007fhere")]
    [InlineData("café")]
    [InlineData("日本")]
    [InlineData("quote\"here")]
    [InlineData("apos'here")]
    [InlineData("paren(here")]
    [InlineData("paren)here")]
    [InlineData("star*here")]
    [InlineData("colon:here")]
    [InlineData("semi;here")]
    [InlineData("comma,here")]
    [InlineData("back\\slash")]
    [InlineData("for/ward")]
    public void A_tag_outside_the_notmuch_safe_charset_is_rejected(string? raw)
    {
        Assert.False(
            Tag.TryParse(raw, out _),
            "A tag must survive a search expression without quoting. Query metacharacters, whitespace and anything "
            + "outside printable ASCII are rejected at construction so no call site has to escape a tag later.");
    }

    [Fact]
    public void An_over_long_tag_is_rejected()
    {
        Assert.True(Tag.TryParse(new string('a', 128), out _));
        Assert.False(Tag.TryParse(new string('a', 129), out _));
    }

    [Fact]
    public void Only_the_four_flag_mirroring_tags_are_system_tags()
    {
        Assert.True(Tag.Unread.IsSystemTag);
        Assert.True(Tag.Flagged.IsSystemTag);
        Assert.True(Tag.Replied.IsSystemTag);
        Assert.True(Tag.Draft.IsSystemTag);

        Assert.False(
            Tag.Inbox.IsSystemTag,
            "'inbox' mirrors no IMAP flag: it is local structure. Treating it as a system tag would make the flag "
            + "bitfield the source of truth for something the server never reports.");
        Assert.False(Tag.Parse("work").IsSystemTag);
    }

    [Fact]
    public void Tag_parse_throws_where_TryParse_returns_false()
    {
        Assert.Throws<FormatException>(() => Tag.Parse("with space"));
        Assert.Equal("work", Tag.Parse("Work").ToString());
    }

    [Theory]
    [InlineData("INBOX", '/', "INBOX")]
    [InlineData("inbox", '/', "INBOX")]
    [InlineData("InBoX", '.', "INBOX")]
    [InlineData("Work", '/', "Work")]
    [InlineData("INBOX.Work.Sub", '.', "INBOX/Work/Sub")]
    [InlineData("INBOX\\Work", '\\', "INBOX/Work")]
    [InlineData("/Work/", '/', "Work")]
    [InlineData(".INBOX.Work.", '.', "INBOX/Work")]
    [InlineData("  Work/Sub  ", '/', "Work/Sub")]
    [InlineData("A.B", '\0', "A.B")]
    public void A_folder_path_normalizes_its_delimiter_and_canonicalizes_inbox(
        string raw,
        char delimiter,
        string expected)
    {
        Assert.True(FolderPath.TryCreate(raw, delimiter, out var path));
        Assert.Equal(expected, path.Value);
    }

    [Theory]
    [InlineData((string?)null, '/')]
    [InlineData("", '/')]
    [InlineData("   ", '/')]
    [InlineData("/", '/')]
    [InlineData("///", '/')]
    [InlineData("...", '.')]
    [InlineData("bad\rname", '/')]
    [InlineData("bad\nname", '/')]
    public void An_unusable_folder_path_is_rejected(string? raw, char delimiter)
    {
        Assert.False(FolderPath.TryCreate(raw, delimiter, out _));
    }

    [Fact]
    public void Inbox_is_case_insensitive_but_every_other_name_keeps_its_case()
    {
        Assert.True(FolderPath.Create("inbox").IsInbox);
        Assert.True(FolderPath.Create("INBOX").IsInbox);
        Assert.True(FolderPath.Create("InBoX").IsInbox);
        Assert.False(FolderPath.Create("Inbox/Sub").IsInbox);

        Assert.True(
            FolderPath.Create("inbox").NameEquals(FolderPath.Create("INBOX")),
            "RFC 3501 defines INBOX as case-insensitive. Two spellings of it must never become two local folders.");

        Assert.Equal("Work", FolderPath.Create("Work").Value);
    }

    [Fact]
    public void Name_comparison_is_case_sensitive_except_on_windows()
    {
        var upper = FolderPath.Create("Work");
        var lower = FolderPath.Create("work");

        Assert.Equal(
            OperatingSystem.IsWindows(),
            upper.NameEquals(lower));
    }

    [Fact]
    public void The_leaf_name_is_everything_after_the_last_delimiter()
    {
        Assert.Equal("Sub", FolderPath.Create("INBOX.Work.Sub", '.').LeafName);
        Assert.Equal("Work", FolderPath.Create("Work").LeafName);
        Assert.Equal("INBOX", FolderPath.Create("inbox").LeafName);
    }

    [Fact]
    public void Folder_path_create_throws_where_TryCreate_returns_false()
    {
        Assert.Throws<ArgumentException>(() => FolderPath.Create("  "));
        Assert.Throws<ArgumentException>(() => FolderPath.Create("bad\rname"));
    }

    [Fact]
    public void The_delimiter_is_part_of_the_record_so_identity_must_go_through_NameEquals()
    {
        var slash = FolderPath.Create("INBOX/Work");
        var dot = FolderPath.Create("INBOX.Work", '.');

        Assert.Equal(slash.Value, dot.Value);
        Assert.True(slash.NameEquals(dot));

        Assert.False(
            slash == dot,
            "Record equality on FolderPath also compares the delimiter the path arrived with, so the same folder "
            + "seen through two servers' delimiters is not '=='. Every folder-identity comparison must use "
            + "NameEquals, which is also the only one that honours the INBOX and Windows case rules.");
    }
}
