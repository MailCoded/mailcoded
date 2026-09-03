using Mailcoded.Tui.Input;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

/// <summary>Escape is both a key and the prefix of every cursor sequence. Holding it until the next
/// byte arrives means it is never a key on its own, which is every escape binding in the app.</summary>
public sealed class EscapeKeyTests
{
    [Fact]
    public void A_lone_escape_arrives_once_the_terminal_goes_quiet()
    {
        var decoder = new InputDecoder();

        Assert.Empty(decoder.Feed("\u001b"u8));

        var only = Assert.Single(decoder.Flush());

        Assert.Equal(ConsoleKey.Escape, only.Key!.Value.Key);
    }

    [Fact]
    public void Flushing_twice_does_not_invent_a_second_escape()
    {
        var decoder = new InputDecoder();

        decoder.Feed("\u001b"u8);

        Assert.Single(decoder.Flush());
        Assert.Empty(decoder.Flush());
    }

    [Fact]
    public void A_quiet_terminal_with_nothing_pending_produces_nothing()
    {
        Assert.Empty(new InputDecoder().Flush());
    }

    /// <summary>A real cursor key must not be split into Escape plus junk by an early flush.</summary>
    [Fact]
    public void A_complete_sequence_is_never_reached_by_the_flush()
    {
        var decoder = new InputDecoder();

        var events = decoder.Feed("\u001b[A"u8);

        Assert.Equal(ConsoleKey.UpArrow, Assert.Single(events).Key!.Value.Key);
        Assert.Empty(decoder.Flush());
    }

    /// <summary>Alt+key arrives as ESC then the key; after the pause both are still delivered.</summary>
    [Fact]
    public void An_escape_followed_by_a_letter_flushes_as_both()
    {
        var decoder = new InputDecoder();

        decoder.Feed("\u001b"u8);
        var typed = decoder.Feed("y"u8);

        var events = typed.Count > 0 ? typed : decoder.Flush();

        Assert.Contains(events, e => e.Key?.Key == ConsoleKey.Escape);
        Assert.Contains(events, e => e.Key?.KeyChar == 'y');
    }

    [Fact]
    public void An_incomplete_sequence_that_never_finishes_still_yields_its_escape()
    {
        var decoder = new InputDecoder();

        decoder.Feed("\u001b[<0;5"u8);

        Assert.Contains(decoder.Flush(), e => e.Key?.Key == ConsoleKey.Escape);
    }
}
