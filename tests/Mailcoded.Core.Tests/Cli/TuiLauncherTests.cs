using System.Reflection;
using Mailcoded.Core.Tests.Architecture;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

/// <summary>The CLI may start the TUI but must never contain it: the boundary is the whole claim.</summary>
public sealed class TuiLauncherTests
{
    [Fact]
    public void The_cli_launches_the_tui_by_name_rather_than_linking_it()
    {
        var cli = ArchitectureAssemblies.Cli;
        Assert.NotNull(cli);

        foreach (var reference in cli.GetReferencedAssemblies())
        {
            Assert.NotEqual(
                ArchitectureAssemblies.TuiAssemblyName,
                reference.Name,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_launcher_names_the_executable_the_installer_installs()
    {
        var launcher = ArchitectureAssemblies.Cli!.GetType("Mailcoded.Cli.Commands.TuiCommand", throwOnError: true)!;
        var name = (string)launcher.GetField("ExecutableName", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

        Assert.Equal(ArchitectureAssemblies.TuiAssemblyName, name);
        Assert.Contains(name, File.ReadAllText(RepositoryFile("scripts/install.sh")), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_shipped_binary_the_installer_places_is_covered_by_the_size_gate()
    {
        var install = File.ReadAllText(RepositoryFile("scripts/install.sh"));
        var gate = File.ReadAllText(RepositoryFile("scripts/size-gate.sh"));

        foreach (var binary in new[] { "mailcoded", "mailcoded-daemon", "mailcoded-mcp", "mailcoded-tui" })
            Assert.Contains(binary, install, StringComparison.Ordinal);

        // Coverage has to come from enumerating the directory. A second list of names beside the
        // installer's is a list that stops matching it without anyone noticing, and a native
        // library never named in either one is exactly what escapes.
        Assert.Contains("find \"$dir\" -type f", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("mailcoded-daemon", gate, StringComparison.Ordinal);
    }

    /// <summary>The installer is the only thing in this project allowed to reach the network, and
    /// only when asked. A default that downloaded would break a promise the README makes.</summary>
    [Fact]
    public void The_installer_downloads_nothing_unless_it_is_asked_to()
    {
        var install = File.ReadAllText(RepositoryFile("scripts/install.sh"));

        Assert.Contains("WANT_MODEL=0", install, StringComparison.Ordinal);
        Assert.Contains("--with-model) WANT_MODEL=1", install, StringComparison.Ordinal);

        var curl = install.IndexOf("curl", StringComparison.Ordinal);
        Assert.True(curl > 0, "the installer no longer has a download to gate");
        Assert.Contains("if [ \"$WANT_MODEL\" = 1 ]", install, StringComparison.Ordinal);
    }

    /// <summary>An unverified weights file is a binary every mail body would then flow through.</summary>
    [Fact]
    public void The_installer_verifies_a_model_before_it_unpacks_it()
    {
        var install = File.ReadAllText(RepositoryFile("scripts/install.sh"));

        var checksum = install.IndexOf("sha256sum", StringComparison.Ordinal);
        var unpack = install.IndexOf("tar -xzf", StringComparison.Ordinal);

        Assert.True(checksum > 0, "the installer does not checksum what it downloads");
        Assert.True(unpack > checksum, "the installer unpacks the archive before checking it");
        Assert.Contains("refusing to install this file", install, StringComparison.Ordinal);
    }

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"No '{relative}' above '{AppContext.BaseDirectory}'.");
    }
}
