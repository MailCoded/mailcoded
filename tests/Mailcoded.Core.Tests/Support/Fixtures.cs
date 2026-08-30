namespace Mailcoded.Core.Tests.Support;

/// <summary>Locates fixtures/eml by walking up from the test binary, never by absolute path.</summary>
public static class Fixtures
{
    public static string EmlDirectory { get; } = Locate();

    public static IReadOnlyList<string> EmlFileNames()
    {
        var names = new List<string>();
        foreach (var path in Directory.EnumerateFiles(EmlDirectory, "*.eml"))
            names.Add(Path.GetFileName(path));

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public static string PathOf(string fileName) => Path.Combine(EmlDirectory, fileName);

    public static byte[] Read(string fileName) => File.ReadAllBytes(PathOf(fileName));

    public static Stream Open(string fileName) =>
        new FileStream(PathOf(fileName), FileMode.Open, FileAccess.Read, FileShare.Read);

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", "eml");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No 'fixtures/eml' directory above '{AppContext.BaseDirectory}'.");
    }
}
