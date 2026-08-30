using System.Buffers;
using System.Text;
using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

/// <summary>
/// Hand-written reader/writer for <c>accounts.config_json</c>: no reflection, no serializer
/// context, nothing for AOT to trim away. <see cref="AccountConfig.SecretRef"/> is a handle into
/// the secret store, never a credential — nothing else secret-bearing may be added here.
/// </summary>
internal static class AccountConfigJson
{
    public static string Write(AccountConfig config)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("secretRef", config.SecretRef);
            writer.WriteString("auth", config.Auth == AuthKind.OAuth2 ? "oauth2" : "password");

            writer.WriteStartObject("imap");
            writer.WriteString("host", config.Imap.Host);
            writer.WriteNumber("port", config.Imap.Port);
            writer.WriteString("security", SecurityToWire(config.Imap.Security));
            if (config.Imap.Username is { } imapUser) writer.WriteString("username", imapUser);
            writer.WriteStartArray("watchFolders");
            foreach (var folder in config.Imap.WatchFolders) writer.WriteStringValue(folder);
            writer.WriteEndArray();
            writer.WriteEndObject();

            if (config.Smtp is { } smtp)
            {
                writer.WriteStartObject("smtp");
                writer.WriteString("host", smtp.Host);
                writer.WriteNumber("port", smtp.Port);
                writer.WriteString("security", SecurityToWire(smtp.Security));
                if (smtp.Username is { } smtpUser) writer.WriteString("username", smtpUser);
                writer.WriteEndObject();
            }

            writer.WriteStartObject("quirks");
            writer.WriteNumber("latched", (int)config.Quirks.Latched);
            if (config.Quirks.PinnedCertificateSha256 is { } pinned)
                writer.WriteString("pinnedCertificateSha256", pinned);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static AccountConfig Read(
        string json,
        AccountId id,
        string email,
        string? displayName,
        ProviderKind provider)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var imapElement = Child(root, "imap");
        var imap = new ImapConfig
        {
            Host = String(imapElement, "host") ?? string.Empty,
            Port = Int(imapElement, "port", 993),
            Security = SecurityFromWire(String(imapElement, "security")),
            Username = String(imapElement, "username"),
            WatchFolders = StringArray(imapElement, "watchFolders"),
        };

        SmtpConfig? smtp = null;
        var smtpElement = Child(root, "smtp");
        if (smtpElement.HasValue && smtpElement.Value.ValueKind == JsonValueKind.Object)
        {
            smtp = new SmtpConfig
            {
                Host = String(smtpElement, "host") ?? string.Empty,
                Port = Int(smtpElement, "port", 587),
                Security = SecurityFromWire(String(smtpElement, "security")),
                Username = String(smtpElement, "username"),
            };
        }

        var quirksElement = Child(root, "quirks");
        var quirks = new ServerQuirksConfig
        {
            Latched = (ServerQuirks)Int(quirksElement, "latched", 0),
            PinnedCertificateSha256 = String(quirksElement, "pinnedCertificateSha256"),
        };

        return new AccountConfig
        {
            Id = id,
            Email = email,
            DisplayName = displayName,
            Provider = provider,
            Imap = imap,
            Smtp = smtp,
            Auth = String(root, "auth") == "oauth2" ? AuthKind.OAuth2 : AuthKind.Password,
            SecretRef = String(root, "secretRef") ?? string.Empty,
            Quirks = quirks,
        };
    }

    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : null;

    private static string? String(JsonElement? element, string name)
    {
        if (!element.HasValue) return null;
        var parent = element.Value;
        if (parent.ValueKind != JsonValueKind.Object) return null;
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int Int(JsonElement? element, string name, int fallback)
    {
        if (!element.HasValue) return fallback;
        var parent = element.Value;
        if (parent.ValueKind != JsonValueKind.Object) return fallback;
        return parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : fallback;
    }

    private static IReadOnlyList<string> StringArray(JsonElement? element, string name)
    {
        if (!element.HasValue) return [];
        var parent = element.Value;
        if (parent.ValueKind != JsonValueKind.Object) return [];
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];

        var items = new List<string>();
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                items.Add(text);
        return items;
    }

    private static string SecurityToWire(SecureSocket security) => security switch
    {
        SecureSocket.None => "none",
        SecureSocket.SslOnConnect => "ssl",
        SecureSocket.StartTls => "starttls",
        SecureSocket.StartTlsWhenAvailable => "starttls-optional",
        _ => "ssl",
    };

    private static SecureSocket SecurityFromWire(string? value) => value switch
    {
        "none" => SecureSocket.None,
        "ssl" => SecureSocket.SslOnConnect,
        "starttls" => SecureSocket.StartTls,
        "starttls-optional" => SecureSocket.StartTlsWhenAvailable,
        _ => SecureSocket.SslOnConnect,
    };
}
