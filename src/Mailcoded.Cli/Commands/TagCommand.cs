using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

internal static class TagCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        var id = new LocalMessageId(line.RequireId(0, "a message id"));

        var add = new List<Tag>();
        var remove = new List<Tag>();

        for (var i = 1; i < line.Positional.Count; i++)
        {
            var token = line.Positional[i];
            if (token.Length < 2 || token[0] is not ('+' or '-'))
            {
                throw new CliUsageException(
                    $"'{token}' needs a sign: '+name' adds a Tag and '-name' removes one.");
            }

            if (!Tag.TryParse(token[1..], out var tag))
                throw new CliUsageException($"'{token[1..]}' is not a valid Tag name.");

            if (token[0] == '+') add.Add(tag);
            else remove.Add(tag);
        }

        if (add.Count == 0 && remove.Count == 0)
            throw new CliUsageException("Pass at least one '+tag' or '-tag'. Run 'mailcoded help tag'.");

        var envelope = host.Messages.GetEnvelope(id, ct);
        var local = line.Flag("local");

        IMailProvider? provider = null;
        if (!local)
        {
            var account = host.Store.GetAccount(envelope.AccountId, ct)
                ?? throw new StoreException(
                    FailureCategory.NotFound,
                    $"Message {id.Value} belongs to account {envelope.AccountId.Value}, which is gone.");

            provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
        }

        var delta = new TagDelta { Add = add, Remove = remove };
        var result = await host.Messages
            .SetTagsAsync(provider, id, delta, host.Caller, ct)
            .ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("id", id.Value);
            JsonFields.WriteTags(writer, "added", add);
            JsonFields.WriteTags(writer, "removed", remove);
            JsonFields.WriteTags(writer, "tags", result.Tags);
            JsonFields.WriteFlags(writer, "flags", result.Flags);
            writer.WriteBoolean("pushed_to_server", result.PushedToServer);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"{id.Value}: {JsonFields.Tags(result.Tags)}");
        output.Line($"flags: {JsonFields.Flags(result.Flags)}  pushed_to_server={(result.PushedToServer ? "true" : "false")}");
        return ExitCodes.Ok;
    }
}
