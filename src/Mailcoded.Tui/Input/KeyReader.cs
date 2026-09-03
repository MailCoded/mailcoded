using System.Threading.Channels;

namespace Mailcoded.Tui.Input;

/// <summary>Console.ReadKey blocks with no cancellation, so it owns a background thread that
/// nobody waits on at shutdown.</summary>
internal static class KeyReader
{
    public static ChannelReader<ConsoleKeyInfo> Start()
    {
        var channel = Channel.CreateUnbounded<ConsoleKeyInfo>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (!channel.Writer.TryWrite(key)) return;
                }
            }
            catch (InvalidOperationException)
            {
                channel.Writer.TryComplete();
            }
            catch (IOException)
            {
                channel.Writer.TryComplete();
            }
        })
        {
            IsBackground = true,
            Name = "mailcoded-tui-keys",
        };

        thread.Start();
        return channel.Reader;
    }
}
