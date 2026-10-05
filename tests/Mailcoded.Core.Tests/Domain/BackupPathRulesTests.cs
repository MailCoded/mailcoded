using Mailcoded.Core.Domain.Backup;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class BackupPathRulesTests
{
    [Theory]
    [InlineData(@"C:\Users\me\backups\today")]
    [InlineData(@"D:\backups")]
    [InlineData(@"\\server\share\backups")]
    [InlineData("C:/Users/me/backups")]
    [InlineData(@"C:\Users\me\My Backups\2026-10-04")]
    [InlineData(@"C:\a\file.name.with.dots\x")]
    public void Ordinary_windows_paths_are_accepted(string path) => Assert.Null(BackupPathRules.Check(path, windows: true));

    [Theory]
    [InlineData(@"\\?\C:\backups")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\UNC\server\share")]
    public void Device_and_extended_length_prefixes_are_refused(string path) => Assert.NotNull(BackupPathRules.Check(path, windows: true));

    [Theory]
    [InlineData(@"C:\backups\file.txt:hidden")]
    [InlineData(@"C:\backups:stream")]
    [InlineData(@"C:backups:x")]
    [InlineData(@"C:\a\b:c\d")]
    public void An_alternate_data_stream_colon_is_refused(string path) => Assert.NotNull(BackupPathRules.Check(path, windows: true));

    [Theory]
    [InlineData(@"C:\backups\CON")]
    [InlineData(@"C:\backups\nul")]
    [InlineData(@"C:\backups\Aux.txt")]
    [InlineData(@"C:\backups\COM1")]
    [InlineData(@"C:\backups\lpt9.log")]
    [InlineData(@"C:\CON\backups")]
    public void Reserved_device_names_are_refused(string path) => Assert.NotNull(BackupPathRules.Check(path, windows: true));

    [Theory]
    [InlineData(@"C:\backups\name.")]
    [InlineData(@"C:\backups\name ")]
    [InlineData(@"C:\back ups \x")]
    public void Components_ending_in_a_dot_or_space_are_refused(string path) => Assert.NotNull(BackupPathRules.Check(path, windows: true));

    [Theory]
    [InlineData(@"C:\backups\a<b")]
    [InlineData(@"C:\backups\a>b")]
    [InlineData(@"C:\backups\a|b")]
    [InlineData(@"C:\backups\a?b")]
    [InlineData(@"C:\backups\a*b")]
    [InlineData("C:\\backups\\a\"b")]
    public void Characters_windows_forbids_are_refused(string path) => Assert.NotNull(BackupPathRules.Check(path, windows: true));

    [Fact]
    public void A_dot_segment_is_left_for_the_resolver_not_refused_here()
    {
        Assert.Null(BackupPathRules.Check(@"C:\backups\..\other", windows: true));
    }

    [Theory]
    [InlineData("/home/me/backups/today")]
    [InlineData("/home/me/with:colon/x")]
    [InlineData("/tmp/CON")]
    [InlineData("/tmp/a*b?c")]
    [InlineData("/tmp/trailing.")]
    public void On_posix_those_windows_rules_do_not_apply(string path) => Assert.Null(BackupPathRules.Check(path, windows: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_blank_path_is_refused_everywhere(string? path)
    {
        Assert.NotNull(BackupPathRules.Check(path, windows: false));
        Assert.NotNull(BackupPathRules.Check(path, windows: true));
    }

    [Theory]
    [InlineData("/tmp/a\0b")]
    [InlineData("/tmp/a\nb")]
    [InlineData("/tmp/a\u001bb")]
    [InlineData("/tmp/a\rb")]
    public void Control_characters_are_refused_everywhere(string path)
    {
        Assert.NotNull(BackupPathRules.Check(path, windows: false));
        Assert.NotNull(BackupPathRules.Check(path, windows: true));
    }

    [Fact]
    public void An_absurdly_long_path_is_refused()
    {
        Assert.NotNull(BackupPathRules.Check("/" + new string('a', BackupPathRules.MaxPathChars), windows: false));
        Assert.Null(BackupPathRules.Check("/" + new string('a', BackupPathRules.MaxPathChars - 1), windows: false));
    }

    [Theory]
    [InlineData("/data/mail", "/data/mail", false, true)]
    [InlineData("/data/mail/backups", "/data/mail", false, true)]
    [InlineData("/data/mail/", "/data/mail", false, true)]
    [InlineData("/data/mail", "/data/mail/", false, true)]
    [InlineData("/data/mail-copy", "/data/mail", false, false)]
    [InlineData("/data/mailbox/x", "/data/mail", false, false)]
    [InlineData("/data", "/data/mail", false, false)]
    [InlineData("/Data/Mail/x", "/data/mail", false, false)]
    [InlineData("/Data/Mail/x", "/data/mail", true, true)]
    [InlineData("/anything", "/", false, true)]
    [InlineData(@"C:\Users\Me\mailcoded\backups", @"C:\Users\me\MAILCODED", true, true)]
    [InlineData(@"C:\Users\Me\mailcoded-copy", @"C:\Users\me\MAILCODED", true, false)]
    [InlineData(@"D:\mailcoded", @"C:\mailcoded", true, false)]
    public void Containment_respects_segment_boundaries_and_case(string candidate, string root, bool ignoreCase, bool expected) =>
        Assert.Equal(expected, BackupPathRules.IsSameOrInside(candidate, root, ignoreCase));
}
