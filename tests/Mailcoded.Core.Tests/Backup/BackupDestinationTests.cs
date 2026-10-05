using Mailcoded.Core.Application;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Backup;

public sealed class BackupDestinationTests : IDisposable
{
    private readonly TempWorkspace _live = TempWorkspace.Create("dest-live");
    private readonly TempWorkspace _area = TempWorkspace.Create("dest-area");

    public void Dispose()
    {
        _live.Dispose();
        _area.Dispose();
    }

    private string[] LiveRoots => [_live.Root, _live.BlobDirectory, _live.Root];

    private string Resolve(string path, string? home = null) =>
        BackupDestination.Resolve(path, LiveRoots, home ?? _area.Root);

    private string Refusal(string path, string? home = null) =>
        Assert.Throws<ArgumentException>(() => Resolve(path, home)).Message;

    [Fact]
    public void A_new_folder_beside_an_existing_parent_is_accepted()
    {
        var path = Path.Combine(_area.Root, "out");

        Assert.Equal(path, Resolve(path));
    }

    [Fact]
    public void An_existing_empty_folder_is_accepted()
    {
        var path = Path.Combine(_area.Root, "empty");
        Directory.CreateDirectory(path);

        Assert.Equal(path, Resolve(path));
    }

    [Fact]
    public void A_trailing_separator_is_ignored()
    {
        var path = Path.Combine(_area.Root, "out");

        Assert.Equal(path, Resolve(path + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_folder_with_anything_in_it_is_refused()
    {
        var path = Path.Combine(_area.Root, "busy");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".hidden"), "x");

        Assert.Contains("not empty", Refusal(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_is_refused()
    {
        var path = Path.Combine(_area.Root, "file.txt");
        File.WriteAllText(path, "x");

        Assert.Contains("file", Refusal(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Assert.Contains("absolute", Refusal("backups/today"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_parent_is_refused_rather_than_created()
    {
        var path = Path.Combine(_area.Root, "no", "such", "parent");

        Assert.Contains("parent", Refusal(path), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_area.Root, "no")));
    }

    [Fact]
    public void A_filesystem_root_is_refused()
    {
        var root = Path.GetPathRoot(_area.Root)!;

        Assert.Contains("root", Refusal(root), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_refused(string path) => Assert.Throws<ArgumentException>(() => Resolve(path));

    [Fact]
    public void A_path_with_a_control_character_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Resolve(Path.Combine(_area.Root, "a\0b")));
        Assert.Throws<ArgumentException>(() => Resolve(Path.Combine(_area.Root, "a\nb")));
    }

    [Fact]
    public void The_live_store_folder_and_everything_inside_it_is_refused()
    {
        Assert.Contains("overlaps", Refusal(_live.Root), StringComparison.Ordinal);
        Assert.Contains("overlaps", Refusal(Path.Combine(_live.Root, "backups")), StringComparison.Ordinal);
        Assert.Contains("overlaps", Refusal(_live.BlobDirectory), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sibling_whose_name_merely_starts_like_the_store_is_accepted()
    {
        var sibling = _live.Root + "-copy";
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.Equal(sibling, Resolve(sibling, home: sibling));
        }
        finally
        {
            Directory.Delete(sibling);
        }
    }

    [Fact]
    public void A_symbolic_link_as_the_destination_is_refused()
    {
        var target = Path.Combine(_area.Root, "real");
        var link = Path.Combine(_area.Root, "link");
        Directory.CreateDirectory(target);
        Assert.SkipUnless(TryLink(link, target), "Symbolic links are not permitted here.");

        Assert.Contains("symbolic link", Refusal(link), StringComparison.Ordinal);
    }

    [Fact]
    public void A_symbolic_link_in_the_path_that_leads_into_the_store_is_refused()
    {
        var link = Path.Combine(_area.Root, "shortcut");
        Assert.SkipUnless(TryLink(link, _live.Root), "Symbolic links are not permitted here.");

        Assert.Contains("overlaps", Refusal(Path.Combine(link, "backups")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_symbolic_link_in_the_path_that_leads_elsewhere_is_allowed()
    {
        var real = Path.Combine(_area.Root, "elsewhere");
        var link = Path.Combine(_area.Root, "alias");
        Directory.CreateDirectory(real);
        Assert.SkipUnless(TryLink(link, real), "Symbolic links are not permitted here.");

        var path = Path.Combine(link, "out");
        Assert.Equal(path, Resolve(path));
    }

    [Fact]
    public void A_circular_link_is_refused_not_looped_on()
    {
        var a = Path.Combine(_area.Root, "a");
        var b = Path.Combine(_area.Root, "b");
        Assert.SkipUnless(TryLink(a, b) && TryLink(b, a), "Symbolic links are not permitted here.");

        Assert.Throws<ArgumentException>(() => Resolve(Path.Combine(a, "out")));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".vscode")]
    [InlineData(".obsidian")]
    [InlineData(".idea")]
    public void A_folder_inside_a_workspace_is_refused(string marker)
    {
        var workspace = Path.Combine(_area.Root, "project");
        Directory.CreateDirectory(Path.Combine(workspace, marker));

        var message = Refusal(Path.Combine(workspace, "backup"));

        Assert.Contains("workspace", message, StringComparison.Ordinal);
        Assert.Contains(marker, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_workspace_file_marks_its_folder()
    {
        var workspace = Path.Combine(_area.Root, "multi");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "my.code-workspace"), "{}");

        Assert.Contains("my.code-workspace", Refusal(Path.Combine(workspace, "deep")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_workspace_several_levels_up_still_counts()
    {
        var workspace = Path.Combine(_area.Root, "project");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        var deep = Path.Combine(workspace, "a", "b");
        Directory.CreateDirectory(deep);

        Assert.Contains("workspace", Refusal(Path.Combine(deep, "out")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dotfiles_repository_in_the_home_folder_does_not_block_every_backup()
    {
        var home = Path.Combine(_area.Root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".git"));
        var backups = Path.Combine(home, "backups");
        Directory.CreateDirectory(backups);

        var path = Path.Combine(backups, "today");

        Assert.Equal(path, Resolve(path, home));
    }

    [Fact]
    public void A_git_folder_above_the_home_folder_is_not_consulted_either()
    {
        var outer = Path.Combine(_area.Root, "outer");
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        var home = Path.Combine(outer, "home");
        Directory.CreateDirectory(home);

        var path = Path.Combine(home, "today");

        Assert.Equal(path, Resolve(path, home));
    }

    [Fact]
    public void A_folder_named_like_a_marker_but_not_a_marker_is_fine()
    {
        var folder = Path.Combine(_area.Root, "gitlike");
        Directory.CreateDirectory(Path.Combine(folder, "git"));

        var path = Path.Combine(folder, "out");

        Assert.Equal(path, Resolve(path));
    }

    private static bool TryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
