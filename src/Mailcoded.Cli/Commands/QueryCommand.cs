using System.Globalization;
using System.Text.Json;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// The mediated read-only SQL surface. The gate, the query_only connection and the row cap are
/// Core's; this command never opens a connection of its own and never hands out a file handle.
/// </summary>
internal static class QueryCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var sql = line.RequireValue("sql");
        var maxRows = line.Int("max-rows", 1, 1000) ?? 0;

        var result = await host.Search.ExecuteSqlAsync(host.Caller, sql, maxRows, ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            JsonFields.WriteStrings(writer, "columns", result.Columns);
            writer.WriteNumber("row_count", result.Rows.Count);
            JsonFields.WritePaging(writer, null, result.Truncated);

            writer.WriteStartArray("rows");
            foreach (var row in result.Rows)
            {
                writer.WriteStartArray();
                foreach (var cell in row) WriteCell(writer, cell);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line(string.Join("\t", result.Columns));
        foreach (var row in result.Rows)
        {
            var cells = new List<string>(row.Count);
            foreach (var cell in row) cells.Add(SafeText.Line(Render(cell), 120));
            output.Line(string.Join("\t", cells));
        }

        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"[{result.Rows.Count} row(s), truncated={(result.Truncated ? "true" : "false")}]"));
        return ExitCodes.Ok;
    }

    /// <summary>Written by hand: SQLite hands back <c>object?</c>, which no source generator can shape.</summary>
    private static void WriteCell(Utf8JsonWriter writer, object? cell)
    {
        switch (cell)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                if (double.IsFinite(number)) writer.WriteNumberValue(number);
                else writer.WriteStringValue(number.ToString(CultureInfo.InvariantCulture));
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case byte[] blob:
                writer.WriteStringValue(Convert.ToBase64String(blob));
                break;
            default:
                writer.WriteStringValue(Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static string Render(object? cell) => cell switch
    {
        null or DBNull => string.Empty,
        string text => text,
        byte[] blob => "<" + blob.Length.ToString(CultureInfo.InvariantCulture) + " bytes>",
        _ => Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
