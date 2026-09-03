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
        {
            Assert.Contains(binary, install, StringComparison.Ordinal);
            Assert.Contains(binary, gate, StringComparison.Ordinal);
        }
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
