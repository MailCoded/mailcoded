using System.Globalization;
using System.Reflection;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

/// <summary>
/// Forward-only embedded migrations keyed on <c>PRAGMA user_version</c>. An applied migration is
/// never edited; a schema change is always a new numbered file.
/// </summary>
internal static class Migrator
{
    private const string ResourcePrefix = "Mailcoded.Core.Store.Migrations.";

    public static int Apply(DbSession session)
    {
        var current = (int)session.ExecScalarInt64("PRAGMA user_version");

        foreach (var migration in Discover())
        {
            if (migration.Version <= current) continue;

            session.BeginImmediate();
            try
            {
                session.Exec(migration.Sql);
                session.Exec(string.Create(
                    CultureInfo.InvariantCulture,
                    $"PRAGMA user_version = {migration.Version}"));
                session.Commit();
            }
            catch
            {
                session.Rollback();
                throw;
            }

            current = migration.Version;
        }

        return current;
    }

    private static List<Migration> Discover()
    {
        var assembly = typeof(Migrator).Assembly;
        var migrations = new List<Migration>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = name[ResourcePrefix.Length..];
            var underscore = fileName.IndexOf('_');
            if (underscore <= 0) continue;
            if (!int.TryParse(fileName.AsSpan(0, underscore), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                continue;

            migrations.Add(new Migration(version, name, ReadResource(assembly, name)));
        }

        migrations.Sort(static (a, b) => a.Version.CompareTo(b.Version));
        return migrations;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new StoreException(FailureCategory.NotFound, $"Migration resource '{name}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private readonly record struct Migration(int Version, string ResourceName, string Sql);
}
