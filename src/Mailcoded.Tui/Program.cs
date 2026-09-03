using Mailcoded.Protocol.Client;
using Mailcoded.Tui.Input;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui;

internal static class Program
{
    private const string Version = "0.1.0";

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        if (args.Contains("--version"))
        {
            Console.Out.WriteLine(Version);
            return 0;
        }

        var store = Value(args, "--store");

        if (!WindowsConsole.TryEnableVirtualTerminal())
        {
            Console.Error.WriteLine("This console cannot render ANSI. Try Windows Terminal.");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        var writer = new TerminalWriter(Console.Out);
        MailcodedClient? client = null;

        try
        {
            client = await MailcodedClient.ConnectAsync(
                DaemonLaunch.Discover(store), "mailcoded-tui", Version, shutdown.Token).ConfigureAwait(false);

            writer.EnterFullScreen();
            return await App.RunAsync(client, writer, shutdown.Token).ConfigureAwait(false);
        }
        catch (DaemonDisconnectedException ex)
        {
            writer.LeaveFullScreen();
            Console.Error.WriteLine(ex.Message);
            if (client is not null) Console.Error.WriteLine(client.Diagnostics);
            return 3;
        }
        catch (RpcException ex)
        {
            writer.LeaveFullScreen();
            Console.Error.WriteLine($"The daemon refused the connection: {ex.Message}");
            return 4;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            writer.LeaveFullScreen();
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];

        return null;
    }

    private const string Usage = """
        mailcoded-tui — a terminal client for the mailcoded daemon

          mailcoded-tui [--store <path>]

        It spawns mailcoded-daemon and speaks Content-Length JSON-RPC over stdio,
        exactly as a third-party client would. Set MAILCODED_DAEMON to choose the
        executable; otherwise it looks beside this binary, then on PATH.

        It asks for plaintext bodies only. A terminal has no sandbox, so this client
        never requests format:html.
        """;
}
