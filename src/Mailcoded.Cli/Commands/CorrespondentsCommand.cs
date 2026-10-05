using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Correspondents;

namespace Mailcoded.Cli.Commands;

internal static class CorrespondentsCommand
{
    private const int NameColumns = 60;

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var prefix = line.Positional.Count > 0 ? line.Positional[0] : null;
        var limit = line.Int("limit", 1, CorrespondentRanking.MaxLimit);
        var account = host.RequireAccount(line, ct);

        var (found, more) = host.Correspondents.SuggestPage(account.Id, prefix, limit, ct);

        await host.Audit.InfoAsync(
            CliAuditEvents.Correspondents,
            account.Id,
            host.Caller,
            AuditText.Fields(("returned", AuditText.Number(found.Count))),
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            writer.WriteNumber("count", found.Count);
            JsonFields.WritePaging(writer, null, more);

            writer.WriteStartArray("correspondents");
            foreach (var each in found)
            {
                writer.WriteStartObject();
                writer.WriteString("address", each.Address);
                JsonFields.WriteText(writer, "display_name", each.DisplayName);
                writer.WriteNumber("uses", each.Count);
                JsonFields.WriteDate(writer, "last_used_utc", each.LastUsedUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        foreach (var each in found)
        {
            var name = SafeText.Line(each.DisplayName, NameColumns);
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"{SafeText.Line(each.Address, 254)}  {each.Count,5}x  {each.LastUsedUtc:yyyy-MM-dd}  {name}").TrimEnd());
        }

        return ExitCodes.Ok;
    }
}
