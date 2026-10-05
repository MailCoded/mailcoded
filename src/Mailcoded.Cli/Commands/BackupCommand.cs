using System.Globalization;
using Mailcoded.Core.Domain.Backup;

namespace Mailcoded.Cli.Commands;

/// <summary>Writes a backup directory. Core owns the gate, the destination rules and the audit rows.</summary>
internal static class BackupCommand
{
    public const string SecretsNote =
        "Secrets are not in the backup. After a restore every account must be signed in again.";

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var directory = Path.GetFullPath(line.RequirePositional(0, "a destination directory"));

        var result = await host.Backups.CreateAsync(host.Caller, directory, ct).ConfigureAwait(false);
        var manifest = result.Manifest;

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteString("directory", result.Directory);
            writer.WriteString("manifest", Path.Combine(result.Directory, BackupManifest.ManifestFileName));
            writer.WriteNumber("format_version", manifest.FormatVersion);
            writer.WriteString("mailcoded_version", manifest.MailcodedVersion);
            writer.WriteNumber("store_schema_version", manifest.SchemaVersion);
            JsonFields.WriteDate(writer, "created_utc", manifest.CreatedUtc);
            writer.WriteNumber("database_bytes", manifest.DatabaseBytes);
            writer.WriteString("database_sha256", manifest.DatabaseSha256);
            writer.WriteNumber("external_blob_count", manifest.Blobs.Count);
            writer.WriteNumber("external_blob_bytes", manifest.BlobBytes);

            writer.WriteStartObject("tables");
            foreach (var table in manifest.Tables.OrderBy(static t => t.Name, StringComparer.Ordinal))
                writer.WriteNumber(table.Name, table.Rows);
            writer.WriteEndObject();

            writer.WriteBoolean("secrets_included", false);
            writer.WriteString("note", SecretsNote);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"Backup written to {SafeText.Line(result.Directory, 200)}");
        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"schema v{manifest.SchemaVersion}, {manifest.DatabaseBytes} bytes of database, {manifest.Blobs.Count} attachment file(s) ({manifest.BlobBytes} bytes)"));
        output.Line(SecretsNote);
        output.Line($"Check it any time with: mailcoded backup-verify {SafeText.Line(result.Directory, 200)}");
        return ExitCodes.Ok;
    }
}
