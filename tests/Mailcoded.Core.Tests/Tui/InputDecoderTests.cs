using System.Text;
using Mailcoded.Tui.Input;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

/// <summary>This decoder replaced Console.ReadKey, so every binding the app has must survive it.</summary>
public sealed class InputDecoderTests
{
    [Theory]
    [InlineData("j", ConsoleKey.None, 'j')]
    [InlineData("G", ConsoleKey.None, 'G')]
    [InlineData("?", ConsoleKey.None, '?')]
    [InlineData("/", ConsoleKey.None, '/')]
    [InlineData("\r", ConsoleKey.Enter, '\r')]
    [InlineData("\n", ConsoleKey.Enter, '\r')]
    [InlineData("\t", ConsoleKey.Tab, '\t')]
    [InlineData("\u007f", ConsoleKey.Backspace, '\b')]
    public void A_plain_key_decodes_to_itself(string input, ConsoleKey key, char c)
    {
        var only = Assert.Single(Decode(input));

        Assert.NotNull(only.Key);
        Assert.Equal(key, only.Key!.Value.Key);
        Assert.Equal(c, only.Key.Value.KeyChar);
    }

    [Theory]
    [InlineData("\u001b[A", ConsoleKey.UpArrow)]
    [InlineData("\u001b[B", ConsoleKey.DownArrow)]
    [InlineData("\u001b[C", ConsoleKey.RightArrow)]
    [InlineData("\u001b[D", ConsoleKey.LeftArrow)]
    [InlineData("\u001b[H", ConsoleKey.Home)]
    [InlineData("\u001b[F", ConsoleKey.End)]
    [InlineData("\u001b[5~", ConsoleKey.PageUp)]
    [InlineData("\u001b[6~", ConsoleKey.PageDown)]
    [InlineData("\u001b[3~", ConsoleKey.Delete)]
    [InlineData("\u001bOA", ConsoleKey.UpArrow)]
    [InlineData("\u001bOD", ConsoleKey.LeftArrow)]
    public void A_cursor_sequence_decodes_to_its_key(string input, ConsoleKey expected)
    {
        var only = Assert.Single(Decode(input));

        Assert.Equal(expected, only.Key!.Value.Key);
    }

    [Fact]
    public void Ctrl_s_is_the_preview_key_the_composer_binds()
    {
        var only = Assert.Single(Decode("\u0013"));

        Assert.Equal(ConsoleKey.S, only.Key!.Value.Key);
        Assert.True(only.Key.Value.Modifiers.HasFlag(ConsoleModifiers.Control));
    }

    /// <summary>Escape then a letter is two presses, not a swallowed prefix.</summary>
    [Fact]
    public void A_bare_escape_is_escape_and_the_next_key_still_arrives()
    {
        var events = Decode("\u001bx");

        Assert.Equal(2, events.Count);
        Assert.Equal(ConsoleKey.Escape, events[0].Key!.Value.Key);
        Assert.Equal('x', events[1].Key!.Value.KeyChar);
    }

    [Fact]
    public void An_escape_on_its_own_waits_rather_than_guessing()
    {
        Assert.Empty(new InputDecoder().Feed("\u001b"u8));
    }

    [Fact]
    public void A_left_click_reports_where_it_landed_zero_based()
    {
        var only = Assert.Single(Decode("\u001b[<0;10;5M"));

        Assert.NotNull(only.Mouse);
        Assert.Equal(MouseAction.Press, only.Mouse!.Value.Action);
        Assert.Equal(9, only.Mouse.Value.Column);
        Assert.Equal(4, only.Mouse.Value.Row);
    }

    [Fact]
    public void A_release_is_distinguished_from_a_press()
    {
        Assert.Equal(MouseAction.Release, Assert.Single(Decode("\u001b[<0;10;5m")).Mouse!.Value.Action);
    }

    [Theory]
    [InlineData("\u001b[<64;3;3M", MouseAction.WheelUp)]
    [InlineData("\u001b[<65;3;3M", MouseAction.WheelDown)]
    public void The_wheel_decodes_in_both_directions(string input, MouseAction expected)
    {
        Assert.Equal(expected, Assert.Single(Decode(input)).Mouse!.Value.Action);
    }

    /// <summary>A wide terminal exceeds what the older X10 encoding can express at all.</summary>
    [Fact]
    public void A_click_past_column_223_survives()
    {
        var only = Assert.Single(Decode("\u001b[<0;300;40M"));

        Assert.Equal(299, only.Mouse!.Value.Column);
    }

    [Fact]
    public void A_drag_is_ignored_rather_than_treated_as_a_click()
    {
        Assert.Empty(Decode("\u001b[<32;10;5M"));
    }

    /// <summary>Split reads are the normal case on a pty; half a report must not become typing.</summary>
    [Fact]
    public void A_sequence_split_across_reads_is_still_one_event()
    {
        var decoder = new InputDecoder();

        Assert.Empty(decoder.Feed("\u001b[<0;1"u8));
        Assert.Empty(decoder.Feed("2;3"u8));

        var only = Assert.Single(decoder.Feed("4M"u8));

        Assert.Equal(11, only.Mouse!.Value.Column);
        Assert.Equal(33, only.Mouse.Value.Row);
    }

    [Fact]
    public void A_partial_escape_never_leaks_its_bytes_as_typed_text()
    {
        var decoder = new InputDecoder();

        Assert.Empty(decoder.Feed("\u001b["u8));
        Assert.Empty(decoder.Feed("<0;5"u8));

        Assert.Single(decoder.Feed(";5M"u8));
    }

    [Fact]
    public void Several_keys_in_one_read_all_arrive()
    {
        var events = Decode("jjk");

        Assert.Equal(3, events.Count);
        Assert.Equal(['j', 'j', 'k'], events.Select(e => e.Key!.Value.KeyChar));
    }

    [Fact]
    public void A_multibyte_character_is_one_key_not_several()
    {
        var only = Assert.Single(Decode("é"));

        Assert.Equal('é', only.Key!.Value.KeyChar);
    }

    /// <summary>Garbage must not wedge the decoder into never emitting again.</summary>
    [Fact]
    public void An_unterminated_sequence_is_eventually_abandoned()
    {
        var decoder = new InputDecoder();

        for (var i = 0; i < 20; i++) decoder.Feed("\u001b[999"u8);

        var recovered = decoder.Feed("j"u8);

        Assert.Contains(recovered, e => e.Key?.KeyChar == 'j');
    }

    private static IReadOnlyList<InputEvent> Decode(string input) =>
        new InputDecoder().Feed(Encoding.UTF8.GetBytes(input));
}
