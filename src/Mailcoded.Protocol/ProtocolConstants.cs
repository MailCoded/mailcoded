namespace Mailcoded.Protocol;

/// <summary>Wire-level constants shared by every client of the JSON-RPC surface (SPEC §5.6).</summary>
public static class ProtocolConstants
{
    /// <summary>The negotiated protocol version. Bump only on a breaking DTO change.</summary>
    public const int Version = 1;

    public const string JsonRpcVersion = "2.0";
}

/// <summary>Method names, exactly as they appear on the wire.</summary>
public static class RpcMethods
{
    public const string Initialize = "initialize";
    public const string SecretSet = "secret.set";
    public const string AccountAdd = "account.add";
    public const string AccountList = "account.list";
    public const string FolderList = "folder.list";
    public const string Sync = "sync";
    public const string Search = "search";
    public const string ThreadGet = "thread.get";
    public const string MessageGet = "message.get";
    public const string AttachmentGet = "attachment.get";
    public const string TagsSet = "tags.set";
    public const string MessageMove = "message.move";
    public const string SendPreview = "send.preview";
    public const string Send = "send";
    public const string WatchSubscribe = "watch.subscribe";
    public const string Stats = "stats";
    public const string Health = "health";
    public const string AccountTest = "account.test";
    public const string OutboxList = "outbox.list";
    public const string Shutdown = "shutdown";
}

/// <summary>Server-to-client notification method names. Notifications never carry an id.</summary>
public static class RpcNotifications
{
    public const string MailAdded = "notify.mail.added";
    public const string FolderUpdated = "notify.folder.updated";
    public const string SyncError = "notify.sync.error";
}

/// <summary>Accepted values of <see cref="MessageGetParams.Format"/>.</summary>
public static class MessageFormats
{
    public const string Text = "text";
    public const string Html = "html";
    public const string Raw = "raw";
}

/// <summary>Accepted values of <see cref="SearchParams.Order"/>.</summary>
public static class SearchOrders
{
    public const string Relevance = "relevance";
    public const string Date = "date";
}

/// <summary>Wire values for <c>Providers.SecureSocket</c>. The daemon maps these to the enum.</summary>
public static class SecurityModes
{
    public const string None = "none";
    public const string SslOnConnect = "sslOnConnect";
    public const string StartTls = "startTls";
    public const string StartTlsWhenAvailable = "startTlsWhenAvailable";
}

/// <summary>Wire values for <c>Providers.AuthKind</c>.</summary>
public static class AuthKinds
{
    public const string Password = "password";
    public const string OAuth2 = "oauth2";
}

/// <summary>Wire values for <c>Providers.ProviderKind</c>, mirroring <c>ProviderKindExtensions</c>.</summary>
public static class ProviderKinds
{
    public const string Imap = "imap";
    public const string Graph = "graph";
    public const string Jmap = "jmap";
    public const string Gmail = "gmail";
}

/// <summary>Values of <see cref="HealthDto.Status"/> and <see cref="AccountHealthDto.Connection"/>.</summary>
public static class HealthStates
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Error = "error";

    public const string Connected = "connected";
    public const string Connecting = "connecting";
    public const string Disconnected = "disconnected";

    public const string AuthOk = "ok";
    public const string AuthRequired = "auth-required";
    public const string AuthUnknown = "unknown";
}

/// <summary>Values of <see cref="SendResult.State"/>, mirroring <c>Domain.Outbox.OutboxState</c>.</summary>
public static class OutboxStates
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}
