using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Daemon;

namespace Mailcoded.Core.Tests.Daemon;

/// <summary>The real framing, JSON-RPC loop and dispatcher, in process, over a temp store.</summary>
internal sealed class DaemonTranscript : IAsyncDisposable
{
    public const string UpdateEnvVar = "MAILCODED_GOLDEN_UPDATE";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly SurfaceWorkspace _workspace;
    private readonly StderrLog _log;
    private readonly RpcChannel _watchChannel;
    private readonly DaemonHost _host;

    private DaemonTranscript(SurfaceWorkspace workspace, StderrLog log, DaemonHost host, RpcChannel watchChannel)
    {
        _workspace = workspace;
        _log = log;
        _host = host;
        _watchChannel = watchChannel;
    }

    public string StoreDirectory => _workspace.Root;

    public static async Task<DaemonTranscript> StartAsync(CancellationToken ct)
    {
        DaemonInfo.EnableTranscriptMode();

        var workspace = new SurfaceWorkspace("daemon-golden");
        var log = new StderrLog(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);

        DaemonHost host;

        // The posture matrix is read once at construction, so the capability block is only
        // deterministic if the agent-policy variables are unset while the host is being built.
        using (EnvironmentScope.Set(
                   (AgentPolicyOptions.SendEnvVar, null),
                   (AgentPolicyOptions.EnableSqlEnvVar, null),
                   (AgentPolicyOptions.ApprovedRecipientsEnvVar, null)))
        {
            host = DaemonHost.Create(workspace.DatabasePath, log, isPrimary: true);
        }

        try
        {
            var channel = new RpcChannel(new FrameWriter(new MemoryStream()), log);
            host.AttachWatch(channel);

            await SeedAsync(host, ct).ConfigureAwait(false);
            return new DaemonTranscript(workspace, log, host, channel);
        }
        catch (Exception)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>Frames one request, runs the server loop to EOF, and returns the single response frame.</summary>
    public async Task<string> ExchangeAsync(byte[] requestBody, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        var input = new MemoryStream(Frame(requestBody), writable: false);
        var output = new MemoryStream();
        var channel = new RpcChannel(new FrameWriter(output), _log);

        try
        {
            var server = new JsonRpcServer(
                new FrameReader(PipeReader.Create(input)),
                channel,
                new RpcDispatcher(_host, _log),
                _log);

            await server.RunAsync(ct, ct).ConfigureAwait(false);
            await server.DrainAsync(Deadline, ct).ConfigureAwait(false);
            await channel.DrainAsync(Deadline, ct).ConfigureAwait(false);
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        return SingleFrame(output.ToArray());
    }

    private static async Task SeedAsync(DaemonHost host, CancellationToken ct)
    {
        var accountId = await StoreSeeder.AddAccountAsync(host.Store, ct).ConfigureAwait(false);
        var folderId = await StoreSeeder.AddInboxAsync(host.Store, accountId, ct).ConfigureAwait(false);

        await StoreSeeder.AddMessageAsync(
            host.Store,
            folderId,
            uid: 1,
            messageId: "golden-1@example.test",
            subject: "Quarterly invoice",
            from: "Ada <ada@example.test>",
            to: "golden@example.test",
            dateUtc: new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
            bodyText: "The invoice total is 42 dollars.",
            size: 1234,
            ct: ct).ConfigureAwait(false);

        await StoreSeeder.AddMessageAsync(
            host.Store,
            folderId,
            uid: 2,
            messageId: "golden-2@example.test",
            subject: "Lunch plans",
            from: "Bob <bob@example.test>",
            to: "golden@example.test",
            dateUtc: new DateTimeOffset(2024, 1, 3, 4, 5, 6, TimeSpan.Zero),
            bodyText: "Sandwiches at noon.",
            size: 2345,
            ct: ct).ConfigureAwait(false);

        await host.Store.RecountFolderAsync(folderId, ct).ConfigureAwait(false);
    }

    private static byte[] Frame(byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");

        var framed = new byte[header.Length + body.Length];
        header.CopyTo(framed, 0);
        body.CopyTo(framed, header.Length);
        return framed;
    }

    private static string SingleFrame(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

        if (separator < 0)
            throw new InvalidOperationException("The daemon produced no Content-Length framed response.");

        var body = text[(separator + 4)..];

        if (body.IndexOf("Content-Length:", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The daemon produced more than one response frame.");

        return body;
    }

    public async ValueTask DisposeAsync()
    {
        await _watchChannel.DisposeAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
        _workspace.Dispose();
    }
}

/// <summary>Locates the golden directory from the test binary, wherever the build put it.</summary>
internal static class GoldenFiles
{
    private static readonly Lazy<string> Directory = new(Locate, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string PathFor(string name) => Path.Combine(Directory.Value, name);

    public static bool UpdateRequested()
    {
        var raw = Environment.GetEnvironmentVariable(DaemonTranscript.UpdateEnvVar);
        return !string.IsNullOrWhiteSpace(raw) && raw.Trim() is "1" or "true" or "TRUE";
    }

    public static byte[] ReadRequest(string name) => File.ReadAllBytes(PathFor(name));

    public static string ReadResponse(string name) =>
        File.Exists(PathFor(name)) ? File.ReadAllText(PathFor(name)).ReplaceLineEndings("\n") : string.Empty;

    public static void WriteResponse(string name, string canonical) =>
        File.WriteAllText(PathFor(name), canonical.ReplaceLineEndings("\n") + "\n");

    private static string Locate()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);

        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, "tests", "Mailcoded.Core.Tests", "golden");
            if (System.IO.Directory.Exists(candidate)) return candidate;
            probe = probe.Parent;
        }

        throw new DirectoryNotFoundException(
            "tests/Mailcoded.Core.Tests/golden was not found above " + AppContext.BaseDirectory + ".");
    }
}
