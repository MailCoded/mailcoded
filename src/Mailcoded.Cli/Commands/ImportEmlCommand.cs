using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Loads .eml files into the store so they parse, index and thread exactly like synced mail.
/// Purely local: nothing is uploaded, and no file outside the given directory is touched.
/// </summary>
internal static class ImportEmlCommand
{
    private const int BatchSize = 200;
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const string LocalAccountEmail = "local@import.mailcoded.test";

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var directory = line.RequirePositional(0, "a directory of .eml files");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"No directory at '{directory}'.");

        var account = await ResolveAccountAsync(host, line, ct).ConfigureAwait(false);
        var folderId = await ResolveFolderAsync(host, account.Id, line.Value("folder"), ct).ConfigureAwait(false);

        var search = line.Flag("recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = new List<string>(Directory.EnumerateFiles(directory, "*.eml", search));
        files.Sort(StringComparer.Ordinal);

        var imported = 0;
        var skipped = 0;
        var failures = new List<(string File, string Reason)>();
        var pending = new List<PendingImport>(BatchSize);

        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var path = files[index];
            var uid = new Uid((uint)(index + 1));

            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > MaxFileBytes)
                {
                    skipped++;
                    failures.Add((path, info.Length == 0 ? "empty file" : "larger than the 64 MiB import limit"));
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var parsed = host.Parser.Parse(bytes, info.LastWriteTimeUtc, ct);
                var blob = await host.Store.StoreBlobAsync(bytes, ct).ConfigureAwait(false);
                pending.Add(new PendingImport(uid, parsed, blob, bytes.LongLength));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                skipped++;
                failures.Add((path, ex.Message));
                continue;
            }

            if (pending.Count < BatchSize) continue;

            imported += await FlushAsync(host, folderId, pending, ct).ConfigureAwait(false);
            pending.Clear();
        }

        if (pending.Count > 0) imported += await FlushAsync(host, folderId, pending, ct).ConfigureAwait(false);

        await host.Store.RecountFolderAsync(folderId, ct).ConfigureAwait(false);

        await host.Audit.InfoAsync(
            CliAuditEvents.Import,
            account.Id,
            host.Caller,
            AuditText.Fields(
                ("folder", AuditText.Number(folderId.Value)),
                ("files", AuditText.Number(files.Count)),
                ("imported", AuditText.Number(imported)),
                ("skipped", AuditText.Number(skipped))),
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteNumber("folder_id", folderId.Value);
            writer.WriteNumber("files", files.Count);
            writer.WriteNumber("imported", imported);
            writer.WriteNumber("skipped", skipped);

            writer.WriteStartArray("failures");
            foreach (var (file, reason) in failures)
            {
                writer.WriteStartObject();
                writer.WriteString("file", SafeText.Line(Path.GetFileName(file), 200));
                writer.WriteString("reason", SafeText.Line(reason, 200));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("next", "mailcoded search '<term>' --json");
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"imported {imported} of {files.Count} file(s) into folder {folderId.Value}; {skipped} skipped"));

        foreach (var (file, reason) in failures)
            output.Line($"  skipped {SafeText.Line(Path.GetFileName(file), 80)}: {SafeText.Line(reason, 120)}");

        return ExitCodes.Ok;
    }

    private static async Task<int> FlushAsync(
        CliHost host,
        FolderId folderId,
        List<PendingImport> pending,
        CancellationToken ct)
    {
        var envelopes = new List<RemoteEnvelope>(pending.Count);
        foreach (var item in pending) envelopes.Add(ToEnvelope(item));

        await host.Store.IngestEnvelopesAsync(folderId, envelopes, host.Threader, ct).ConfigureAwait(false);

        var stored = 0;
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();

            var messageId = host.Store.FindMessage(folderId, item.Uid, ct);
            if (messageId is not { } id) continue;

            await host.Store
                .SetBodyTextAsync(id, item.Message.BodyText, item.Blob.Id, item.Message.HasAttachments, ct)
                .ConfigureAwait(false);
            stored++;
        }

        return stored;
    }

    private static RemoteEnvelope ToEnvelope(PendingImport item)
    {
        var references = new List<string>(item.Message.References.Count);
        foreach (var reference in item.Message.References) references.Add(reference.Value);

        return new RemoteEnvelope
        {
            Uid = item.Uid,
            // Imported mail has never been read on this machine; there is no server to ask.
            Flags = MessageFlags.Unread,
            MessageIdHeader = item.Message.MessageId?.Value,
            References = references,
            InReplyTo = item.Message.InReplyTo?.Value,
            Subject = item.Message.Subject,
            From = item.Message.From,
            To = item.Message.To,
            Cc = item.Message.Cc,
            DateUtc = item.Message.DateUtc,
            Size = item.Size,
            HasAttachments = item.Message.HasAttachments,
        };
    }

    private static async Task<AccountConfig> ResolveAccountAsync(CliHost host, CommandLine line, CancellationToken ct)
    {
        if (line.Value("account") is not null) return host.RequireAccount(line, ct);

        var accounts = host.Store.ListAccounts(ct);
        if (accounts.Count > 0) return host.RequireAccount(line, ct);

        var email = line.Value("email") ?? LocalAccountEmail;
        var accountId = await host.Accounts.AddAsync(
            new AddAccountRequest
            {
                Email = email,
                DisplayName = "Local import",
                Provider = ProviderKind.Imap,
                Imap = new ImapConfig { Host = "localhost", Port = 993, Security = SecureSocket.None },
                Auth = AuthKind.Password,
            },
            host.Caller,
            ct).ConfigureAwait(false);

        return host.Store.GetAccount(accountId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, "The account created for import disappeared.");
    }

    private static async Task<FolderId> ResolveFolderAsync(
        CliHost host,
        AccountId accountId,
        string? name,
        CancellationToken ct)
    {
        var path = FolderPath.Create(string.IsNullOrWhiteSpace(name) ? FolderPath.Inbox : name);
        var role = path.IsInbox ? FolderRole.Inbox : FolderRole.None;

        var ids = await host.Store
            .UpsertFoldersAsync(accountId, [new RemoteFolder { Path = path, Role = role }], ct)
            .ConfigureAwait(false);

        return ids.Count > 0
            ? ids[0]
            : throw new StoreException(FailureCategory.NotFound, $"Could not create folder '{path.Value}'.");
    }

    private readonly record struct PendingImport(Uid Uid, ParsedMessage Message, BlobRef Blob, long Size);
}
