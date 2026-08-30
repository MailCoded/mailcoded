using System.Globalization;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using App = Mailcoded.Core.Application;

namespace Mailcoded.Daemon;

/// <summary>
/// Hand-written Core-to-wire mapping (CLAUDE invariant 12: no mapping library). Every method here
/// is a pure projection; no gate, no policy, and no I/O lives in this file.
/// </summary>
internal static class WireMapper
{
    public const int MaxNotificationEnvelopes = 20;

    public static string Instant(DateTimeOffset value) =>
        DaemonInfo.Deterministic ? DaemonInfo.TranscriptInstant : IsoTime.ToWire(value);

    public static string? InstantOrNull(DateTimeOffset? value)
    {
        if (value is not { } present) return null;
        return DaemonInfo.Deterministic ? DaemonInfo.TranscriptInstant : IsoTime.ToWire(present);
    }

    public static EnvelopeDto ToDto(EnvelopeRow row, IReadOnlyList<Tag> tags, string? snippet = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new EnvelopeDto
        {
            Id = row.Id.Value,
            AccountId = row.AccountId.Value,
            FolderId = row.FolderId.Value,
            ThreadKey = row.ThreadKey?.Value,
            MessageId = row.MessageId?.Value,
            Subject = row.Subject,
            From = row.From,
            To = row.To,
            Cc = row.Cc,
            Date = IsoTime.ToWire(row.DateUtc),
            Flags = FlagNames.From(row.Flags),
            Tags = ToTagNames(tags),
            HasAttachments = row.HasAttachments,
            Size = row.Size,
            BodyFetched = row.BodyFetched,
            Snippet = snippet,
        };
    }

    public static EnvelopeDto ToDto(StoreSearchHit hit, AccountId accountId, IReadOnlyList<Tag> tags)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return new EnvelopeDto
        {
            Id = hit.Id.Value,
            AccountId = accountId.Value,
            FolderId = hit.FolderId.Value,
            Subject = hit.Subject,
            From = hit.From,
            Date = IsoTime.ToWire(hit.DateUtc),
            Flags = FlagNames.From(hit.Flags),
            Tags = ToTagNames(tags),
            Snippet = hit.Snippet,
        };
    }

    public static EnvelopeDto ToDto(EnvelopeSummary summary, AccountId accountId, FolderId folderId)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new EnvelopeDto
        {
            Id = summary.Id.Value,
            AccountId = accountId.Value,
            FolderId = folderId.Value,
            Subject = summary.Subject,
            From = summary.From,
            Date = IsoTime.ToWire(summary.DateUtc),
            Flags = FlagNames.From(summary.Flags),
            Tags = ToTagNames(TagFlagMap.SystemTagsOf(summary.Flags)),
        };
    }

    public static FolderDto ToDto(FolderSummary folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        return new FolderDto
        {
            Id = folder.Id.Value,
            AccountId = folder.AccountId.Value,
            Name = folder.Path.Value,
            Role = folder.Role.ToWireValue(),
            Unread = folder.UnreadCount,
            Total = folder.TotalCount,
        };
    }

    public static AccountDto ToDto(AccountConfig account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var smtp = account.Smtp;

        return new AccountDto
        {
            Id = account.Id.Value,
            Email = account.Email,
            DisplayName = account.DisplayName,
            Provider = account.Provider.ToWireValue(),
            Auth = account.Auth == AuthKind.OAuth2 ? AuthKinds.OAuth2 : AuthKinds.Password,
            Imap = new ImapConfigDto
            {
                Host = account.Imap.Host,
                Port = account.Imap.Port,
                Security = FromSecurity(account.Imap.Security),
                Username = account.Imap.Username,
                WatchFolders = account.Imap.WatchFolders,
            },
            Smtp = smtp is null
                ? null
                : new SmtpConfigDto
                {
                    Host = smtp.Host,
                    Port = smtp.Port,
                    Security = FromSecurity(smtp.Security),
                    Username = smtp.Username,
                },
            SecretRef = account.SecretRef,
            Quirks = QuirkNames(account.Quirks.Latched),
        };
    }

    public static FolderSyncStatDto ToStatDto(FolderSummary folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        return new FolderSyncStatDto
        {
            FolderId = folder.Id.Value,
            AccountId = folder.AccountId.Value,
            Name = folder.Path.Value,
            HighestModSeq = ModSeqWire.ToWire(folder.HighestModSeq),
            UidNext = folder.UidNext is { } next ? (long)next.Value : null,
            Unread = folder.UnreadCount,
            Total = folder.TotalCount,
            LastSyncUtc = InstantOrNull(folder.LastSyncUtc),
        };
    }

    public static StatsDto ToDto(App.DaemonStats stats, IReadOnlyList<FolderSyncStatDto> folders)
    {
        ArgumentNullException.ThrowIfNull(stats);

        return new StatsDto
        {
            DaemonVersion = DaemonInfo.Version,
            ProtocolVersion = ProtocolConstants.Version,
            UptimeMs = DaemonInfo.Measured(stats.Process.UptimeMs),
            WorkingSetBytes = DaemonInfo.Measured(stats.Process.WorkingSetBytes),
            GcHeapBytes = DaemonInfo.Measured(stats.Process.GcHeapBytes),
            GcCommittedBytes = DaemonInfo.Measured(stats.Process.GcCommittedBytes),
            GcTotalAllocatedBytes = DaemonInfo.Measured(stats.Process.TotalAllocatedBytes),
            Gen0Collections = DaemonInfo.Measured(stats.Process.Gen0Collections),
            Gen1Collections = DaemonInfo.Measured(stats.Process.Gen1Collections),
            Gen2Collections = DaemonInfo.Measured(stats.Process.Gen2Collections),
            ThreadCount = DaemonInfo.Measured(stats.Process.ThreadCount),
            HandleCount = DaemonInfo.Measured(stats.Process.HandleCount),
            OpenConnections = stats.OpenConnections,
            SchemaVersion = stats.Store.SchemaVersion,
            DatabaseSizeBytes = DaemonInfo.Measured(stats.Store.DatabaseSizeBytes),
            WalSizeBytes = DaemonInfo.Measured(stats.Store.WalSizeBytes),
            BlobDirectorySizeBytes = DaemonInfo.Measured(stats.Store.BlobDirectorySizeBytes),
            TableCounts = stats.Store.TableCounts,
            Folders = folders,
        };
    }

    public static AttachmentDto ToDto(ParsedAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        return new AttachmentDto
        {
            Index = attachment.Index,
            Filename = AttachmentNaming.ToSaveAsFileName(attachment.FileName, attachment.Index, attachment.MimeType),
            Mime = attachment.MimeType,
            Size = attachment.Size,
            IsInline = attachment.IsInline,
            ContentId = attachment.ContentId,
        };
    }

    public static IReadOnlyList<AttachmentDto> ToDtos(IReadOnlyList<ParsedAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return [];

        var dtos = new List<AttachmentDto>(attachments.Count);
        foreach (var attachment in attachments) dtos.Add(ToDto(attachment));
        return dtos;
    }

    public static SendPreviewDto ToDto(App.SendPreview preview, bool bodyTruncated)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return new SendPreviewDto
        {
            From = preview.From.Value,
            To = ToAddressStrings(preview.To),
            Cc = ToAddressStrings(preview.Cc),
            Bcc = ToAddressStrings(preview.Bcc),
            Subject = preview.Subject,
            BodyPreview = preview.BodyPreview,
            BodyTruncated = bodyTruncated,
            SizeBytes = preview.SizeBytes,
            MessageId = preview.MessageId.Value,
            RequiresSmtpUtf8 = preview.RequiresSmtpUtf8,
            Warnings = GateWarnings(preview.Gate),
        };
    }

    /// <summary>Stable slugs only — never the gate's prose, which can name a recipient.</summary>
    private static IReadOnlyList<string> GateWarnings(App.SendGateDecision gate)
    {
        if (gate.Allowed) return [];

        return gate.Reason switch
        {
            App.PolicyDenialReason.SendDisabled => new[] { "send-disabled" },
            App.PolicyDenialReason.RecipientNotApproved => new[] { "recipient-not-approved" },
            App.PolicyDenialReason.RateLimited => new[] { "send-rate-limited" },
            _ => new[] { "send-denied" },
        };
    }

    public static IReadOnlyList<string> ToAddressStrings(IReadOnlyList<EmailAddress>? addresses)
    {
        if (addresses is null || addresses.Count == 0) return [];

        var values = new List<string>(addresses.Count);
        foreach (var address in addresses) values.Add(address.Value);
        return values;
    }

    public static IReadOnlyList<string> ToTagNames(IReadOnlyList<Tag>? tags)
    {
        if (tags is null || tags.Count == 0) return [];

        var names = new List<string>(tags.Count);
        foreach (var tag in tags) names.Add(tag.Value);
        return names;
    }

    public static App.MessageBodyFormat ToFormat(string? value) => value switch
    {
        null or "" or MessageFormats.Text => App.MessageBodyFormat.Text,
        MessageFormats.Html => App.MessageBodyFormat.Html,
        MessageFormats.Raw => App.MessageBodyFormat.Raw,
        _ => throw new ArgumentException($"format must be one of '{MessageFormats.Text}', '{MessageFormats.Html}', '{MessageFormats.Raw}'."),
    };

    public static SearchOrder ToOrder(string? value) => value switch
    {
        null or "" or SearchOrders.Relevance => SearchOrder.Relevance,
        SearchOrders.Date => SearchOrder.Date,
        _ => throw new ArgumentException($"order must be '{SearchOrders.Relevance}' or '{SearchOrders.Date}'."),
    };

    public static SecureSocket ToSecurity(string? value) => value switch
    {
        null or "" or SecurityModes.SslOnConnect => SecureSocket.SslOnConnect,
        SecurityModes.None => SecureSocket.None,
        SecurityModes.StartTls => SecureSocket.StartTls,
        SecurityModes.StartTlsWhenAvailable => SecureSocket.StartTlsWhenAvailable,
        _ => throw new ArgumentException("security must be one of none, sslOnConnect, startTls, startTlsWhenAvailable."),
    };

    public static string FromSecurity(SecureSocket value) => value switch
    {
        SecureSocket.None => SecurityModes.None,
        SecureSocket.StartTls => SecurityModes.StartTls,
        SecureSocket.StartTlsWhenAvailable => SecurityModes.StartTlsWhenAvailable,
        _ => SecurityModes.SslOnConnect,
    };

    public static ProviderKind ToProviderKind(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ProviderKind.Imap;

        try
        {
            return ProviderKindExtensions.FromWireValue(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException("provider must be one of imap, graph, jmap, gmail.");
        }
    }

    public static AuthKind ToAuthKind(string? value) => value switch
    {
        null or "" or AuthKinds.Password => AuthKind.Password,
        AuthKinds.OAuth2 => AuthKind.OAuth2,
        _ => throw new ArgumentException("auth.kind must be 'password' or 'oauth2'."),
    };

    public static ImapConfig ToImapConfig(ImapConfigDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (string.IsNullOrWhiteSpace(dto.Host)) throw new ArgumentException("imap.host is required.");

        return new ImapConfig
        {
            Host = dto.Host.Trim(),
            Port = RequirePort(dto.Port, "imap.port"),
            Security = ToSecurity(dto.Security),
            Username = dto.Username,
            WatchFolders = dto.WatchFolders ?? [],
        };
    }

    public static SmtpConfig? ToSmtpConfig(SmtpConfigDto? dto)
    {
        if (dto is null) return null;
        if (string.IsNullOrWhiteSpace(dto.Host)) throw new ArgumentException("smtp.host is required when smtp is present.");

        return new SmtpConfig
        {
            Host = dto.Host.Trim(),
            Port = RequirePort(dto.Port, "smtp.port"),
            Security = ToSecurity(dto.Security),
            Username = dto.Username,
        };
    }

    public static EmailAddress RequireAddress(string? raw, string field)
    {
        if (!EmailAddress.TryParse(raw, out var address))
            throw new ArgumentException($"{field} is not a valid email address.");

        return address;
    }

    public static IReadOnlyList<EmailAddress> RequireAddresses(IReadOnlyList<string>? raw, string field)
    {
        if (raw is null || raw.Count == 0) return [];

        var addresses = new List<EmailAddress>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
            addresses.Add(RequireAddress(raw[i], string.Create(CultureInfo.InvariantCulture, $"{field}[{i}]")));

        return addresses;
    }

    public static IReadOnlyList<Tag> RequireTags(IReadOnlyList<string>? raw, string field)
    {
        if (raw is null || raw.Count == 0) return [];

        var tags = new List<Tag>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            if (!Tag.TryParse(raw[i], out var tag))
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{field}[{i}] is not a valid tag."));

            tags.Add(tag);
        }

        return tags;
    }

    public static IReadOnlyList<MessageId> RequireMessageIds(IReadOnlyList<string>? raw, string field)
    {
        if (raw is null || raw.Count == 0) return [];

        var ids = new List<MessageId>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            if (!MessageId.TryParse(raw[i], out var id))
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{field}[{i}] is not a valid Message-ID."));

            ids.Add(id);
        }

        return ids;
    }

    private static int RequirePort(int port, string field)
    {
        if (port is < 1 or > 65535) throw new ArgumentException($"{field} must be between 1 and 65535.");
        return port;
    }

    /// <summary>One stable slug per latched flag; a comma-joined enum name is not a wire contract.</summary>
    private static IReadOnlyList<string> QuirkNames(ServerQuirks quirks)
    {
        if (quirks == ServerQuirks.None) return [];

        var names = new List<string>(6);
        if (quirks.HasFlag(ServerQuirks.QresyncBroken)) names.Add("qresync-broken");
        if (quirks.HasFlag(ServerQuirks.CondstoreBroken)) names.Add("condstore-broken");
        if (quirks.HasFlag(ServerQuirks.RequiresId)) names.Add("requires-id");
        if (quirks.HasFlag(ServerQuirks.NoDeletedFlag)) names.Add("no-deleted-flag");
        if (quirks.HasFlag(ServerQuirks.LowConnectionLimit)) names.Add("low-connection-limit");
        if (quirks.HasFlag(ServerQuirks.SelfSignedLocalhost)) names.Add("self-signed-localhost");
        return names;
    }
}
