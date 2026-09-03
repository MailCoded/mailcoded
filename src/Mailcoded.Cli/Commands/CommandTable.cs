namespace Mailcoded.Cli.Commands;

internal delegate Task<int> CommandHandler(CliHost host, CommandLine line, CliOutput output, CancellationToken ct);

internal sealed record CliCommandDefinition(string Name, VerbSpec Spec, CommandHandler Run);

/// <summary>
/// Audit event names this host writes for calls Core does not already record. Search, tag, send
/// and raw SQL are audited inside Core; adding a row here would double-log them.
/// </summary>
internal static class CliAuditEvents
{
    public const string Read = "cli_read";
    public const string Thread = "cli_thread";
    public const string Folders = "cli_folders";
    public const string Stats = "cli_stats";
    public const string Health = "cli_health";
    public const string Import = "cli_import";
}

/// <summary>The verb table. There is no delete, expunge, trash or purge entry, and never will be.</summary>
internal static class CommandTable
{
    private static readonly CliCommandDefinition[] Commands =
    [
        new CliCommandDefinition("search",
            new VerbSpec(["limit", "cursor", "account", "folder", "order"], ["no-snippet"]),
            SearchCommand.RunAsync),

        new CliCommandDefinition("read",
            new VerbSpec(["max-chars", "skip-chars"], ["plaintext", "no-fetch"]),
            ReadCommand.RunAsync),

        new CliCommandDefinition("thread",
            new VerbSpec(["limit"], []),
            ThreadCommand.RunAsync),

        new CliCommandDefinition("tag",
            new VerbSpec([], ["local"]),
            TagCommand.RunAsync),

        new CliCommandDefinition("draft",
            new VerbSpec(
                ["to", "cc", "bcc", "subject", "body-file", "from", "reply-to", "in-reply-to", "account"],
                ["body-stdin"]),
            DraftCommand.RunAsync),

        new CliCommandDefinition("send-preview",
            new VerbSpec([], []),
            SendPreviewCommand.RunAsync),

        new CliCommandDefinition("send-draft",
            new VerbSpec(["confirm-token"], ["no-append"]),
            SendDraftCommand.RunAsync),

        new CliCommandDefinition("query",
            new VerbSpec(["sql", "max-rows"], ["read-only"]),
            QueryCommand.RunAsync),

        new CliCommandDefinition("stats", new VerbSpec([], []), StatsCommand.RunAsync),

        new CliCommandDefinition("health", new VerbSpec([], []), HealthCommand.RunAsync),

        new CliCommandDefinition("folders", new VerbSpec(["account"], []), FoldersCommand.RunAsync),

        new CliCommandDefinition("sync", new VerbSpec(["account", "folder"], []), SyncCommand.RunAsync),

        new CliCommandDefinition("setup",
            new VerbSpec(["email", "display-name", "imap-host", "imap-port", "smtp-host", "smtp-port", "client-id", "tenant"], []),
            SetupCommand.RunAsync),

        new CliCommandDefinition("account add",
            new VerbSpec(
                [
                    "email", "display-name", "imap-host", "imap-port", "imap-security", "imap-user",
                    "smtp-host", "smtp-port", "smtp-security", "smtp-user", "secret-ref",
                ],
                ["password-stdin", "no-password"]),
            AccountAddCommand.RunAsync),

        new CliCommandDefinition("account list", new VerbSpec([], []), AccountListCommand.RunAsync),

        new CliCommandDefinition("account test",
            new VerbSpec(["account"], ["no-smtp"]),
            AccountTestCommand.RunAsync),

        new CliCommandDefinition("account reauth",
            new VerbSpec(["account", "client-id", "tenant"], []),
            AccountReauthCommand.RunAsync),

        new CliCommandDefinition("attachments",
            new VerbSpec(["save", "out"], ["no-fetch", "overwrite"]),
            AttachmentsCommand.RunAsync),

        new CliCommandDefinition("reply",
            new VerbSpec(["body", "body-file"], ["all", "no-quote", "no-fetch"]),
            ReplyCommand.RunAsync),

        new CliCommandDefinition("outbox", new VerbSpec(["state"], []), OutboxCommand.RunAsync),

        new CliCommandDefinition("move", new VerbSpec(["folder"], ["yes"]), MoveCommand.RunAsync),

        new CliCommandDefinition("archive", new VerbSpec([], ["yes"]), MoveCommand.RunArchiveAsync),

        new CliCommandDefinition("account forget",
            new VerbSpec(["account"], ["yes"]),
            AccountForgetCommand.RunAsync),

        new CliCommandDefinition("import-eml",
            new VerbSpec(["account", "folder", "email"], ["recursive"]),
            ImportEmlCommand.RunAsync),
    ];

    public static CliCommandDefinition? Find(string verb)
    {
        foreach (var command in Commands)
            if (string.Equals(command.Name, verb, StringComparison.Ordinal)) return command;

        return null;
    }

    public static string Names()
    {
        var names = new List<string>(Commands.Length + 3);
        foreach (var command in Commands) names.Add(command.Name);
        names.Add("version");
        names.Add("help");
        names.Add("tui");
        names.Sort(StringComparer.Ordinal);
        return string.Join(", ", names);
    }
}
