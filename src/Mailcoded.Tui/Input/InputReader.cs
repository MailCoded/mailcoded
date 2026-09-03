using System.Threading.Channels;

namespace Mailcoded.Tui.Input;

/// <summary>Reads stdin and decodes it. Falls back to Console.ReadKey when the terminal will not
/// go raw, which costs the mouse but keeps every key working.</summary>
internal static class InputReader
{
    public static ChannelReader<InputEvent> Start(bool raw)
    {
        var channel = Channel.CreateUnbounded<InputEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var thread = new Thread(raw ? () => PumpRaw(channel) : () => PumpKeys(channel))
        {
            IsBackground = true,
            Name = "mailcoded-tui-input",
        };

        thread.Start();
        return channel.Reader;
    }

    private static void PumpRaw(Channel<InputEvent> channel)
    {
        var decoder = new InputDecoder();
        var buffer = new byte[1024];

        try
        {
            // Not Console.OpenStandardInput: opening it makes .NET configure the terminal its own
            // way, which puts echo and canonical mode back and swallows every escape sequence.
            using var stdin = OpenTerminal();

            var quiet = 0;

            while (true)
            {
                var read = stdin.Read(buffer, 0, buffer.Length);

                if (read == 0)
                {
                    // VTIME expiry, not end of stream: the terminal simply had nothing to say.
                    if (++quiet > 6000) return;

                    foreach (var next in decoder.Flush())
                        if (!channel.Writer.TryWrite(next)) return;

                    continue;
                }

                if (read < 0) break;

                quiet = 0;

                foreach (var next in decoder.Feed(buffer.AsSpan(0, read)))
                    if (!channel.Writer.TryWrite(next)) return;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        channel.Writer.TryComplete();
    }

    private static Stream OpenTerminal()
    {
        try
        {
            return new FileStream("/dev/tty", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, false);
        }
        catch (Exception)
        {
            return Console.OpenStandardInput();
        }
    }

    private static void PumpKeys(Channel<InputEvent> channel)
    {
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (!channel.Writer.TryWrite(InputEvent.FromKey(key))) return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }

        channel.Writer.TryComplete();
    }
}
