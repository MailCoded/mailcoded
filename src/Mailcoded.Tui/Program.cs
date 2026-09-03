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

        if (args.Contains("--check")) return await CheckAsync(store).ConfigureAwait(false);

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

        // Not Console.Out: .NET's console initialisation reapplies its own terminal attributes,
        // which undoes raw mode and takes the mouse with it.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = false };
        var writer = new TerminalWriter(stdout);
        MailcodedClient? client = null;
        RawMode? raw = null;

        try
        {
            client = await MailcodedClient.ConnectAsync(
                DaemonLaunch.Discover(store), "mailcoded-tui", Version, shutdown.Token).ConfigureAwait(false);

            raw = RawMode.Enter();

            writer.EnterFullScreen(mouse: raw.IsRaw);
            return await App.RunAsync(client, writer, raw.IsRaw, shutdown.Token).ConfigureAwait(false);
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
            // Mouse reporting and the alt screen go first: restoring termios before them would
            // leave a shell echoing raw mouse reports at whoever moves the pointer next.
            writer.LeaveFullScreen();
            raw?.Dispose();

            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Headless: spawn, initialize, report, exit. Proves the wire without a terminal,
    /// which is what a CI smoke test and a confused user both need.</summary>
    private static async Task<int> CheckAsync(string? store)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await using var client = await MailcodedClient
                .ConnectAsync(DaemonLaunch.Discover(store), "mailcoded-tui", Version, deadline.Token)
                .ConfigureAwait(false);

            var accounts = await client.ListAccountsAsync(deadline.Token).ConfigureAwait(false);

            Console.Out.WriteLine($"daemon      {client.DaemonVersion}");
            Console.Out.WriteLine($"methods     {client.Capabilities.Methods.Count}");
            Console.Out.WriteLine($"maxSearch   {client.Capabilities.MaxSearchLimit}");
            Console.Out.WriteLine($"accounts    {accounts.Accounts.Count}");
            Console.Out.WriteLine("ok");
            return 0;
        }
        catch (Exception ex) when (ex is DaemonDisconnectedException or RpcException or OperationCanceledException)
        {
            Console.Error.WriteLine("check failed: " + ex.Message);
            return 3;
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
          mailcoded-tui --check [--store <path>]     connect, report, exit

        It spawns mailcoded-daemon and speaks Content-Length JSON-RPC over stdio,
        exactly as a third-party client would. Set MAILCODED_DAEMON to choose the
        executable; otherwise it looks beside this binary, then on PATH.

        It asks for plaintext bodies only. A terminal has no sandbox, so this client
        never requests format:html.
        """;
}
