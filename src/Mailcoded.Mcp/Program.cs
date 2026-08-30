using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailcoded.Mcp;

internal static class Program
{
    public const string DatabasePathEnvVar = "MAILCODED_DB";

    private const string ServerName = "mailcoded";
    private const string ServerVersion = "0.1.0";

    private static async Task<int> Main(string[] args)
    {
        // stdout carries MCP frames; redirecting Console.Out keeps a stray write off the wire.
        Console.SetOut(Console.Error);

        using var stopping = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stopping.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        using var loggerFactory = new StderrLoggerFactory(StderrLoggerFactory.LevelFromEnvironment());

        try
        {
            await using var host = McpHost.Create(ResolveDatabasePath(args));
            var options = CreateOptions(host);

            await using var transport = new StdioServerTransport(options, loggerFactory);
            await using var server = McpServer.Create(transport, options, loggerFactory);

            await server.RunAsync(stopping.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("mailcoded-mcp: " + ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static McpServerOptions CreateOptions(McpHost host) => new()
    {
        ServerInfo = new Implementation
        {
            Name = ServerName,
            Title = "mailcoded",
            Version = ServerVersion,
        },
        ServerInstructions = ToolText.ServerInstructions,
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
        ToolCollection = ToolCatalog.Build(new MailTools(host)),
        ScopeRequests = false,
    };

    private static string? ResolveDatabasePath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--db", StringComparison.Ordinal)) return args[i + 1];

        var fromEnvironment = Environment.GetEnvironmentVariable(DatabasePathEnvVar);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }
}
