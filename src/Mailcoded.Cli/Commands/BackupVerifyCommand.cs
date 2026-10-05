using System.Globalization;

namespace Mailcoded.Cli.Commands;

/// <summary>Re-reads a backup directory and compares it with its manifest. Exit 9 when anything differs.</summary>
internal static class BackupVerifyCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var directory = Path.GetFullPath(line.RequirePositional(0, "a backup directory"));

        var result = await host.Backups.VerifyAsync(host.Caller, directory, ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson(result.Ok);
            writer.WriteString("directory", result.Directory);

            if (result.Manifest is { } manifest)
            {
                writer.WriteNumber("format_version", manifest.FormatVersion);
                writer.WriteString("mailcoded_version", manifest.MailcodedVersion);
                writer.WriteNumber("store_schema_version", manifest.SchemaVersion);
                JsonFields.WriteDate(writer, "created_utc", manifest.CreatedUtc);
            }

            writer.WriteString("integrity", result.Integrity);
            writer.WriteNumber("blob_files_checked", result.BlobFilesChecked);
            writer.WriteNumber("bytes_checked", result.BytesChecked);
            writer.WriteNumber("problem_count", result.ProblemCount);
            writer.WriteBoolean("problems_truncated", result.ProblemCount > result.Problems.Count);

            writer.WriteStartArray("problems");
            foreach (var problem in result.Problems)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", problem.Kind);
                JsonFields.WriteText(writer, "subject", problem.Subject);
                writer.WriteString("message", problem.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return result.Ok ? ExitCodes.Ok : ExitCodes.Store;
        }

        if (result.Ok)
        {
            output.Line($"Backup at {SafeText.Line(result.Directory, 200)} matches its manifest.");
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"integrity {result.Integrity}, {result.BlobFilesChecked} attachment file(s) and {result.BytesChecked} bytes re-hashed"));
            return ExitCodes.Ok;
        }

        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Backup at {SafeText.Line(result.Directory, 200)} does NOT match its manifest: {result.ProblemCount} problem(s)."));

        foreach (var problem in result.Problems)
        {
            var subject = problem.Subject is null ? string.Empty : SafeText.Line(problem.Subject, 120) + ": ";
            output.Line($"  [{problem.Kind}] {subject}{SafeText.Line(problem.Message, 240)}");
        }

        if (result.ProblemCount > result.Problems.Count)
            output.Line($"  ... and {(result.ProblemCount - result.Problems.Count).ToString(CultureInfo.InvariantCulture)} more");

        return ExitCodes.Store;
    }
}
