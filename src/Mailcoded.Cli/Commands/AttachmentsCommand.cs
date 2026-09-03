using Mailcoded.Core.Application;
using System.Globalization;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>Lists what is attached to a message, or writes one attachment to disk.</summary>
internal static class AttachmentsCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var id = new Mailcoded.Core.Domain.Primitives.LocalMessageId(line.RequireId(0, "a message id"));
        var save = line.Int("save", 0, 4096);

        IMailProvider? provider = null;
        if (!line.Flag("no-fetch"))
        {
            var account = host.Store.GetAccount(host.Messages.GetEnvelope(id, ct).AccountId, ct);
            if (account is not null)
            {
                try
                {
                    provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
                }
                catch (ProviderException)
                {
                    // A cached body is enough to list attachments; only a fetch needs the server.
                }
            }
        }

        if (save is { } index) return await SaveAsync(host, line, output, provider, id, index, ct).ConfigureAwait(false);

        var attachments = await host.Messages
            .ListAttachmentsAsync(provider, id, host.Caller, ct)
            .ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("id", id.Value);
            writer.WriteNumber("count", attachments.Count);
            writer.WriteStartArray("attachments");
            foreach (var a in attachments)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", a.Index);
                JsonFields.WriteText(writer, "filename", a.FileName);
                writer.WriteString("mime", a.MimeType);
                writer.WriteNumber("size_bytes", a.Size);
                writer.WriteBoolean("inline", a.IsInline);
                writer.WriteString("safe_filename", AttachmentNaming.ToSaveAsFileName(a.FileName, a.Index, a.MimeType));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        if (attachments.Count == 0)
        {
            output.Line("No attachments.");
            return ExitCodes.Ok;
        }

        foreach (var a in attachments)
        {
            output.Line($"{a.Index.ToString(CultureInfo.InvariantCulture)}  "
                + $"{a.FileName ?? "(unnamed)"}  {a.MimeType}  "
                + $"{(a.Size / 1024).ToString(CultureInfo.InvariantCulture)} KB{(a.IsInline ? "  inline" : string.Empty)}");
        }

        output.Blank();
        output.Line($"Save one with: mailcoded attachments {id.Value.ToString(CultureInfo.InvariantCulture)} --save <index> --out <dir>");
        return ExitCodes.Ok;
    }

    private static async Task<int> SaveAsync(
        CliHost host,
        CommandLine line,
        CliOutput output,
        IMailProvider? provider,
        Mailcoded.Core.Domain.Primitives.LocalMessageId id,
        int index,
        CancellationToken ct)
    {
        var content = await host.Messages
            .GetAttachmentAsync(provider, id, index, host.Caller, ct)
            .ConfigureAwait(false);

        // The filename comes from the message and is therefore attacker-controlled: it is
        // sanitized to a bare leaf so it cannot climb out of the directory the human chose.
        var leaf = AttachmentNaming.ToSaveAsFileName(content.FileName, index, content.MimeType);
        var directory = line.Value("out") ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(Path.GetFullPath(directory), leaf);

        if (File.Exists(path) && !line.Flag("overwrite"))
            throw new CliUsageException($"{path} already exists; pass --overwrite to replace it.");

        await File.WriteAllBytesAsync(path, content.Content, ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("id", id.Value);
            writer.WriteNumber("index", index);
            writer.WriteString("path", path);
            writer.WriteNumber("size_bytes", content.Content.Length);
            writer.WriteString("mime", content.MimeType);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"Wrote {path} ({content.Content.Length.ToString(CultureInfo.InvariantCulture)} bytes).");
        return ExitCodes.Ok;
    }
}
