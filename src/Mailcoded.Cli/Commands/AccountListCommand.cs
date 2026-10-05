using System.Globalization;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>Every account this store knows about, with enough detail to tell them apart.</summary>
internal static class AccountListCommand
{
    public static Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var accounts = host.Store.ListAccounts(ct);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("count", accounts.Count);
            writer.WriteStartArray("accounts");
            foreach (var account in accounts)
            {
                var folders = host.Store.ListFolders(account.Id, ct);
                writer.WriteStartObject();
                writer.WriteNumber("id", account.Id.Value);
                writer.WriteString("email", account.Email);
                JsonFields.WriteText(writer, "display_name", account.DisplayName);
                writer.WriteString("provider", account.Provider.ToWireValue());
                writer.WriteString("auth", account.Auth == AuthKind.OAuth2 ? "oauth2" : "password");
                writer.WriteString("imap_host", account.Imap.Host);
                writer.WriteNumber("imap_port", account.Imap.Port);
                JsonFields.WriteText(writer, "smtp_host", account.Smtp?.Host);
                writer.WriteNumber("folders", folders.Count);
                writer.WriteNumber("messages", TotalMessages(folders));
                writer.WriteNumber("unread", TotalUnread(folders));
                JsonFields.WriteDate(writer, "last_sync_utc", LastSync(folders));
                writer.WriteBoolean("save_drafts_to_server", account.SaveDraftsToServer);
                writer.WriteStartArray("trusted_authserv_ids");
                foreach (var id in account.EffectiveAuthservTrust().Patterns) writer.WriteStringValue(id);
                writer.WriteEndArray();
                writer.WriteStartArray("identities");
                foreach (var identity in account.Identities) writer.WriteStringValue(identity.Address.Value);
                writer.WriteEndArray();
                writer.WriteStartArray("signatures");
                foreach (var signature in account.Signatures.Items) writer.WriteStringValue(signature.Name);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return Task.FromResult(ExitCodes.Ok);
        }

        if (accounts.Count == 0)
        {
            output.Line("No accounts yet. Run 'mailcoded setup' to add one.");
            return Task.FromResult(ExitCodes.Ok);
        }

        foreach (var account in accounts)
        {
            var folders = host.Store.ListFolders(account.Id, ct);
            var sync = LastSync(folders);

            output.Line($"{account.Id.Value.ToString(CultureInfo.InvariantCulture)}  {account.Email}");
            output.Line($"    auth      {(account.Auth == AuthKind.OAuth2 ? "oauth2" : "password")}");
            output.Line($"    imap      {account.Imap.Host}:{account.Imap.Port.ToString(CultureInfo.InvariantCulture)}");
            if (account.Smtp is { } smtp)
                output.Line($"    smtp      {smtp.Host}:{smtp.Port.ToString(CultureInfo.InvariantCulture)}");
            output.Line($"    mail      {TotalMessages(folders).ToString(CultureInfo.InvariantCulture)} in "
                + $"{folders.Count.ToString(CultureInfo.InvariantCulture)} folders, "
                + $"{TotalUnread(folders).ToString(CultureInfo.InvariantCulture)} unread");
            output.Line($"    synced    {(sync is { } at ? at.ToString("u", CultureInfo.InvariantCulture) : "never")}");

            var trusted = account.EffectiveAuthservTrust().Patterns;
            output.Line($"    verified  {(trusted.Count == 0 ? "no authserv-id trusted" : "trusts " + string.Join(", ", trusted))}");

            if (account.SaveDraftsToServer) output.Line("    drafts    also saved to the server's Drafts Folder");
            if (account.Identities.Count > 0)
                output.Line($"    sends as  {string.Join(", ", account.Identities.Select(i => i.Address.Value))}");
            if (!account.Signatures.IsEmpty)
                output.Line($"    signatures  {string.Join(", ", account.Signatures.Items.Select(x => x.Name))}");
        }

        return Task.FromResult(ExitCodes.Ok);
    }

    private static int TotalMessages(IReadOnlyList<Mailcoded.Core.Store.FolderSummary> folders)
    {
        var total = 0;
        foreach (var folder in folders) total += folder.TotalCount;
        return total;
    }

    private static int TotalUnread(IReadOnlyList<Mailcoded.Core.Store.FolderSummary> folders)
    {
        var total = 0;
        foreach (var folder in folders) total += folder.UnreadCount;
        return total;
    }

    private static DateTimeOffset? LastSync(IReadOnlyList<Mailcoded.Core.Store.FolderSummary> folders)
    {
        DateTimeOffset? latest = null;
        foreach (var folder in folders)
        {
            if (folder.LastSyncUtc is { } at && (latest is null || at > latest)) latest = at;
        }

        return latest;
    }
}
