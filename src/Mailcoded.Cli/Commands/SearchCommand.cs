using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;

namespace Mailcoded.Cli.Commands;

internal static class SearchCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var query = line.RequirePositional(0, "a query, quoted if it contains spaces");

        AccountId? accountScope = null;
        if (line.Long("account", 1, long.MaxValue) is { } accountId) accountScope = new AccountId(accountId);

        FolderId? folderScope = null;
        if (line.Long("folder", 1, long.MaxValue) is { } folderId) folderScope = new FolderId(folderId);

        var request = new SearchRequest
        {
            Query = query,
            Limit = line.Int("limit", 1, 200) ?? 0,
            Cursor = line.Value("cursor"),
            AccountId = accountScope,
            FolderId = folderScope,
            Order = ParseOrder(line.Value("order")),
            IncludeSnippet = !line.Flag("no-snippet"),
        };

        var meaning = line.Flag("meaning");
        var service = meaning ? host.SearchWithMeaning(ct) : host.Search;
        var results = await service.SearchAsync(request, host.Caller, ct).ConfigureAwait(false);

        // "more matches exist" is what an agent branches on; Core's stricter Truncated marks the
        // subset no cursor can reach, so the two are unioned here and documented in help.
        var more = results.Truncated || results.NextCursor is not null;

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteString("query", query);
            writer.WriteString("route", results.Route.ToString().ToLowerInvariant());
            writer.WriteNumber("count", results.Hits.Count);
            writer.WriteBoolean("relaxed", results.Relaxed);
            writer.WriteBoolean("semantic", results.Semantic);
            JsonFields.WritePaging(writer, results.NextCursor, more);

            writer.WriteStartArray("hits");
            foreach (var hit in results.Hits)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", hit.Id.Value);
                writer.WriteNumber("folder_id", hit.FolderId.Value);
                JsonFields.WriteDate(writer, "date", hit.DateUtc);
                JsonFields.WriteText(writer, "from", hit.From);
                JsonFields.WriteText(writer, "subject", hit.Subject);
                JsonFields.WriteFlags(writer, "flags", hit.Flags);
                JsonFields.WriteText(writer, "snippet", hit.Snippet);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("errors");
            foreach (var error in results.Errors)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", error.Kind.ToString());
                writer.WriteString("message", error.Message);
                writer.WriteNumber("position", error.Position);
                writer.WriteString("token", error.Token);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        WriteHuman(output, line, results, more);
        return ExitCodes.Ok;
    }

    private static void WriteHuman(CliOutput output, CommandLine line, SearchResults results, bool more)
    {
        foreach (var error in results.Errors)
            output.Line($"! {error.Kind}: {SafeText.Line(error.Message, 160)}");

        // Before the hits, not after: unread, these look like exact matches.
        if (results.Relaxed)
            output.Line("~ nothing matched every word, so these match some of them, best first.");

        if (line.Flag("meaning") && !results.Semantic)
            output.Line("~ no model is installed, or nothing has been embedded yet; these match on words alone.");

        foreach (var hit in results.Hits)
        {
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"{hit.Id.Value,8}  {JsonFields.Iso(hit.DateUtc)}  [{JsonFields.Flags(hit.Flags)}]  {SafeText.Line(hit.From, 48)}"));
            output.Line("          " + SafeText.Line(hit.Subject, 96));
            if (hit.Snippet is { Length: > 0 } snippet) output.Line("          " + SafeText.Line(snippet, 160));
        }

        if (line.Flag("quiet")) return;

        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"{results.Hits.Count} hit(s), truncated={(more ? "true" : "false")}"));

        if (results.NextCursor is { } cursor)
            output.Line($"next: mailcoded search '<same query>' --cursor {cursor}");
    }

    private static SearchOrder ParseOrder(string? value) => value switch
    {
        null or "" or "relevance" => SearchOrder.Relevance,
        "date" => SearchOrder.Date,
        _ => throw new CliUsageException($"--order must be 'relevance' or 'date', not '{value}'."),
    };
}
