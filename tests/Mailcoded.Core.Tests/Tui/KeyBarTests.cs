using Mailcoded.Tui;
using Mailcoded.Tui.Views;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

/// <summary>The bar exists so nothing has to be remembered, so what it shows must be what works.</summary>
public sealed class KeyBarTests
{
    [Fact]
    public void A_prompt_offers_only_the_two_keys_a_prompt_has()
    {
        var hints = KeyBar.Entries(new AppState { Prompt = "search: " });

        Assert.Equal(["enter", "esc"], hints.Select(h => h.Key));
    }

    [Fact]
    public void The_confirmation_screen_offers_the_send_key_and_nothing_that_sends_by_accident()
    {
        var hints = KeyBar.Entries(new AppState { Focus = Pane.Confirm });

        Assert.Equal("Y", hints[0].Key);
        Assert.DoesNotContain(hints, h => h.Key == "enter");
    }

    [Theory]
    [InlineData(Pane.Help)]
    [InlineData(Pane.Status)]
    [InlineData(Pane.Outbox)]
    public void An_overlay_offers_only_the_way_out(Pane pane)
    {
        var only = Assert.Single(KeyBar.Entries(new AppState { Focus = pane }));

        Assert.Equal("esc", only.Key);
    }

    [Fact]
    public void Choosing_a_destination_offers_move_rather_than_the_folder_keys()
    {
        var hints = KeyBar.Entries(new AppState { ChoosingDestination = true });

        Assert.Contains(hints, h => h.Label == "move here");
        Assert.DoesNotContain(hints, h => h.Label == "compose");
    }

    [Fact]
    public void Every_clickable_hint_carries_the_key_it_claims_to_be()
    {
        foreach (var state in States())
        {
            foreach (var hint in KeyBar.Entries(state))
            {
                if (hint.Press is not { } press) continue;
                if (hint.Key.Length != 1) continue;

                Assert.Equal(hint.Key[0], press.KeyChar);
            }
        }
    }

    /// <summary>A hint standing for two keys cannot be clicked, because it would have to guess.</summary>
    [Fact]
    public void A_paired_hint_is_not_clickable()
    {
        var hints = KeyBar.Entries(new AppState { Focus = Pane.Folders });

        Assert.Null(hints.Single(h => h.Key == "j/k").Press);
        Assert.NotNull(hints.Single(h => h.Key == "c").Press);
    }

    [Fact]
    public void Segments_never_overlap_and_never_run_past_the_edge()
    {
        foreach (var state in States())
        {
            foreach (var width in new[] { 40, 80, 100, 160, 240 })
            {
                var segments = KeyBar.Place(KeyBar.Entries(state), width);
                var previousEnd = 0;

                foreach (var segment in segments)
                {
                    Assert.True(segment.Column >= previousEnd, "two hints overlapped");
                    Assert.True(
                        segment.Column + segment.Width <= width,
                        $"a hint ran to {segment.Column + segment.Width} of {width}");

                    previousEnd = segment.Column + segment.Width;
                }
            }
        }
    }

    /// <summary>Dropping a whole hint beats clipping one into something unreadable.</summary>
    [Fact]
    public void A_narrow_bar_drops_hints_rather_than_cutting_one_in_half()
    {
        var hints = KeyBar.Entries(new AppState { Focus = Pane.Messages });

        var narrow = KeyBar.Place(hints, 30);
        var wide = KeyBar.Place(hints, 200);

        Assert.True(narrow.Count < wide.Count);
        Assert.All(narrow, s => Assert.Equal(s.Hint.Key.Length + 1 + s.Hint.Label.Length, s.Width));
    }

    [Fact]
    public void A_click_lands_on_the_hint_drawn_under_it()
    {
        var state = new AppState { Focus = Pane.Folders };
        var segment = KeyBar.Place(KeyBar.Entries(state), 200).First(s => s.Hint.Key == "c");

        Assert.Equal("c", KeyBar.At(state, 200, segment.Column)?.Key);
        Assert.Equal("c", KeyBar.At(state, 200, segment.Column + segment.Width - 1)?.Key);
    }

    [Fact]
    public void A_click_in_the_gap_between_hints_selects_neither()
    {
        var state = new AppState { Focus = Pane.Folders };
        var segments = KeyBar.Place(KeyBar.Entries(state), 200);
        var gap = segments[0].Column + segments[0].Width;

        Assert.True(gap < segments[1].Column);
        Assert.Null(KeyBar.At(state, 200, gap));
    }

    private static IEnumerable<AppState> States()
    {
        yield return new AppState { Focus = Pane.Folders };
        yield return new AppState { Focus = Pane.Messages };
        yield return new AppState { Focus = Pane.Confirm };
        yield return new AppState { Focus = Pane.Compose };
        yield return new AppState { Focus = Pane.Help };
        yield return new AppState { Prompt = "tags: " };
        yield return new AppState { ChoosingDestination = true };
    }
}
