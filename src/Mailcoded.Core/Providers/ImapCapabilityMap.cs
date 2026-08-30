using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using ImapFlags = MailKit.MessageFlags;
using MailcodedFlags = Mailcoded.Core.Domain.Primitives.MessageFlags;

namespace Mailcoded.Core.Providers;

/// <summary>Translates MailKit's protocol vocabulary into Domain types. No I/O.</summary>
internal static class ImapCapabilityMap
{
    private const int MaxKeywordLength = 128;
    private const int MaxKeywordCount = 64;

    public static SecureSocketOptions ToSocketOptions(SecureSocket security) => security switch
    {
        SecureSocket.None => SecureSocketOptions.None,
        SecureSocket.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SecureSocket.StartTls => SecureSocketOptions.StartTls,
        SecureSocket.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        _ => SecureSocketOptions.SslOnConnect,
    };

    public static ServerCaps ToServerCaps(ImapCapabilities capabilities, ServerQuirks quirks) => new()
    {
        Condstore = capabilities.HasFlag(ImapCapabilities.CondStore),
        Qresync = capabilities.HasFlag(ImapCapabilities.QuickResync),
        Utf8Accept = capabilities.HasFlag(ImapCapabilities.UTF8Accept) || capabilities.HasFlag(ImapCapabilities.UTF8Only),
        SpecialUse = capabilities.HasFlag(ImapCapabilities.SpecialUse) || capabilities.HasFlag(ImapCapabilities.XList),
        Move = capabilities.HasFlag(ImapCapabilities.Move),
        Idle = capabilities.HasFlag(ImapCapabilities.Idle),
        GmailExtensions = capabilities.HasFlag(ImapCapabilities.GMailExt1),
        Quirks = quirks,
    };

    /// <summary>
    /// Seeds the quirk set from what was latched previously plus what the host and the advertised
    /// capabilities already imply. Runtime detection only ever adds to this.
    /// </summary>
    public static ServerQuirks DetectQuirks(AccountConfig cfg, ImapCapabilities capabilities)
    {
        var quirks = cfg.Quirks.Latched;
        var host = cfg.Imap.Host;

        if (EndsWith(host, "yahoo.com") || EndsWith(host, "yahoodns.net") || EndsWith(host, "aol.com"))
            quirks |= ServerQuirks.RequiresId | ServerQuirks.LowConnectionLimit;

        if (capabilities.HasFlag(ImapCapabilities.GMailExt1) || EndsWith(host, "gmail.com") || EndsWith(host, "googlemail.com"))
            quirks |= ServerQuirks.NoDeletedFlag | ServerQuirks.LowConnectionLimit;

        if (!string.IsNullOrWhiteSpace(cfg.Quirks.PinnedCertificateSha256) && IsLoopbackHost(host))
            quirks |= ServerQuirks.SelfSignedLocalhost;

        return quirks;
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    public static MailcodedFlags ToDomainFlags(ImapFlags flags)
    {
        var result = MailcodedFlags.None;

        // Bit 0 is Unread, the inverse of \Seen.
        if (!flags.HasFlag(ImapFlags.Seen)) result |= MailcodedFlags.Unread;
        if (flags.HasFlag(ImapFlags.Flagged)) result |= MailcodedFlags.Flagged;
        if (flags.HasFlag(ImapFlags.Answered)) result |= MailcodedFlags.Answered;
        if (flags.HasFlag(ImapFlags.Draft)) result |= MailcodedFlags.Draft;
        if (flags.HasFlag(ImapFlags.Deleted)) result |= MailcodedFlags.Deleted;
        if (flags.HasFlag(ImapFlags.Recent)) result |= MailcodedFlags.Recent;

        return result;
    }

    public static ImapFlags ToImapFlags(MailcodedFlags flags)
    {
        var result = ImapFlags.None;

        if (!flags.HasFlag(MailcodedFlags.Unread)) result |= ImapFlags.Seen;
        if (flags.HasFlag(MailcodedFlags.Flagged)) result |= ImapFlags.Flagged;
        if (flags.HasFlag(MailcodedFlags.Answered)) result |= ImapFlags.Answered;
        if (flags.HasFlag(MailcodedFlags.Draft)) result |= ImapFlags.Draft;
        if (flags.HasFlag(MailcodedFlags.Deleted)) result |= ImapFlags.Deleted;

        return result;
    }

    /// <summary>
    /// Splits a delta into the two STORE directions. \Seen crosses over because the Domain bit
    /// is Unread, so adding Unread means removing \Seen.
    /// </summary>
    public static (ImapFlags Add, ImapFlags Remove) ToStoreFlags(MailcodedFlags add, MailcodedFlags remove)
    {
        var addFlags = ImapFlags.None;
        var removeFlags = ImapFlags.None;

        if (add.HasFlag(MailcodedFlags.Unread)) removeFlags |= ImapFlags.Seen;
        if (remove.HasFlag(MailcodedFlags.Unread)) addFlags |= ImapFlags.Seen;

        if (add.HasFlag(MailcodedFlags.Flagged)) addFlags |= ImapFlags.Flagged;
        if (remove.HasFlag(MailcodedFlags.Flagged)) removeFlags |= ImapFlags.Flagged;

        if (add.HasFlag(MailcodedFlags.Answered)) addFlags |= ImapFlags.Answered;
        if (remove.HasFlag(MailcodedFlags.Answered)) removeFlags |= ImapFlags.Answered;

        if (add.HasFlag(MailcodedFlags.Draft)) addFlags |= ImapFlags.Draft;
        if (remove.HasFlag(MailcodedFlags.Draft)) removeFlags |= ImapFlags.Draft;

        if (add.HasFlag(MailcodedFlags.Deleted)) addFlags |= ImapFlags.Deleted;
        if (remove.HasFlag(MailcodedFlags.Deleted)) removeFlags |= ImapFlags.Deleted;

        return (addFlags, removeFlags);
    }

    public static IReadOnlyList<string> ToKeywords(IReadOnlySet<string>? keywords)
    {
        if (keywords is null || keywords.Count == 0) return [];

        var result = new List<string>(Math.Min(keywords.Count, MaxKeywordCount));
        foreach (var keyword in keywords)
        {
            if (result.Count >= MaxKeywordCount) break;
            if (IsSafeKeyword(keyword)) result.Add(keyword);
        }

        return result;
    }

    public static IList<string> ToStorableKeywords(IReadOnlyList<string> keywords)
    {
        var result = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
            if (IsSafeKeyword(keyword)) result.Add(keyword);

        return result;
    }

    /// <summary>An IMAP keyword is an atom: no whitespace, no specials, never a system flag.</summary>
    public static bool IsSafeKeyword(string? keyword)
    {
        if (string.IsNullOrEmpty(keyword) || keyword.Length > MaxKeywordLength) return false;
        if (keyword[0] == '\\') return false;

        foreach (var c in keyword)
        {
            if (c <= 0x20 || c >= 0x7f) return false;
            if (c is '(' or ')' or '{' or '}' or '[' or ']' or '%' or '*' or '"' or '\\') return false;
        }

        return true;
    }

    public static FolderRole ToRole(FolderAttributes attributes, FolderPath path)
    {
        if (path.IsInbox || attributes.HasFlag(FolderAttributes.Inbox)) return FolderRole.Inbox;
        if (attributes.HasFlag(FolderAttributes.Sent)) return FolderRole.Sent;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return FolderRole.Drafts;
        if (attributes.HasFlag(FolderAttributes.Trash)) return FolderRole.Trash;
        if (attributes.HasFlag(FolderAttributes.Junk)) return FolderRole.Junk;
        if (attributes.HasFlag(FolderAttributes.Archive)) return FolderRole.Archive;
        if (attributes.HasFlag(FolderAttributes.All)) return FolderRole.All;

        return RoleFromName(path.LeafName);
    }

    /// <summary>Fallback for servers without SPECIAL-USE or XLIST.</summary>
    public static FolderRole RoleFromName(string leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName)) return FolderRole.None;

        var name = leafName.Trim();
        if (Same(name, "INBOX")) return FolderRole.Inbox;
        if (Same(name, "Sent") || Same(name, "Sent Mail") || Same(name, "Sent Items") || Same(name, "Sent Messages")) return FolderRole.Sent;
        if (Same(name, "Drafts") || Same(name, "Draft")) return FolderRole.Drafts;
        if (Same(name, "Trash") || Same(name, "Deleted") || Same(name, "Deleted Items") || Same(name, "Deleted Messages") || Same(name, "Bin")) return FolderRole.Trash;
        if (Same(name, "Archive") || Same(name, "Archives")) return FolderRole.Archive;
        if (Same(name, "Junk") || Same(name, "Spam") || Same(name, "Junk E-mail") || Same(name, "Junk Email") || Same(name, "Bulk Mail")) return FolderRole.Junk;
        if (Same(name, "All Mail") || Same(name, "All")) return FolderRole.All;

        return FolderRole.None;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWith(string? host, string suffix) =>
        !string.IsNullOrEmpty(host) && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
