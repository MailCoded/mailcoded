using System.Reflection;
using Mailcoded.Protocol;
using Mailcoded.Tui;
using Mailcoded.Tui.Views;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

public sealed class ComposeTests
{
    [Fact]
    public void The_confirm_key_is_not_enter()
    {
        Assert.NotEqual('\r', ConfirmSend.ConfirmKey);
        Assert.NotEqual('\n', ConfirmSend.ConfirmKey);
        Assert.NotEqual(' ', ConfirmSend.ConfirmKey);
    }

    /// <summary>docs/rpc.md §8: never log the token. Views only ever receive AppState, so the
    /// guarantee is structural as long as nothing on this path can carry one.</summary>
    [Fact]
    public void No_type_a_view_can_reach_carries_the_confirm_token()
    {
        foreach (var type in new[] { typeof(AppState), typeof(PendingSend) })
        {
            foreach (var member in Members(type))
            {
                Assert.DoesNotContain("token", member, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_preview_a_view_renders_has_no_token_on_it()
    {
        foreach (var member in Members(typeof(SendPreviewDto)))
            Assert.DoesNotContain("token", member, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_wire_result_that_does_carry_a_token_redacts_itself()
    {
        var result = new SendPreviewResult
        {
            DraftId = 7,
            ConfirmToken = "s3cr3t-one-time-value",
            Preview = Preview(),
        };

        Assert.DoesNotContain("s3cr3t", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Countdown_says_plainly_when_a_confirmation_has_expired()
    {
        var now = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

        Assert.Contains("expired", ConfirmSend.Countdown(now.AddSeconds(-1), now), StringComparison.Ordinal);
        Assert.Contains("30s", ConfirmSend.Countdown(now.AddSeconds(30), now), StringComparison.Ordinal);
        Assert.Contains("does not expire", ConfirmSend.Countdown(null, now), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a@x.test, b@y.test", 2)]
    [InlineData("a@x.test; b@y.test ;", 2)]
    [InlineData("   ", 0)]
    [InlineData("", 0)]
    public void Addresses_split_on_both_separators(string raw, int expected) =>
        Assert.Equal(expected, DraftBuffer.SplitAddresses(raw).Count);

    [Fact]
    public void Typing_into_a_header_lands_at_the_caret()
    {
        var draft = new DraftBuffer();

        foreach (var c in "abc") draft.Insert(c);
        draft.MoveCaret(-1, 0);
        draft.Insert('X');

        Assert.Equal("abXc", draft.To);
    }

    [Fact]
    public void Backspace_at_the_start_of_a_body_line_joins_it_to_the_previous_one()
    {
        var draft = new DraftBuffer { Field = DraftField.Body };

        foreach (var c in "one") draft.Insert(c);
        draft.NewLine();
        foreach (var c in "two") draft.Insert(c);
        draft.MoveCaret(-int.MaxValue / 2, 0);
        draft.Backspace();

        Assert.Equal("onetwo", draft.BodyText);
        Assert.Equal(0, draft.BodyLine);
    }

    [Fact]
    public void A_body_keeps_its_line_breaks_through_a_round_trip()
    {
        var draft = new DraftBuffer();
        draft.SetBody("first\r\nsecond\n\nfourth");

        Assert.Equal("first\nsecond\n\nfourth", draft.BodyText);
    }

    [Fact]
    public void Field_navigation_stops_at_both_ends()
    {
        var draft = new DraftBuffer();

        draft.NextField(-5);
        Assert.Equal(DraftField.To, draft.Field);

        draft.NextField(99);
        Assert.Equal(DraftField.Body, draft.Field);
    }

    [Fact]
    public void A_quote_is_capped_so_a_thread_cannot_grow_without_bound()
    {
        var body = string.Join('\n', Enumerable.Range(0, 500).Select(i => "line " + i));
        var quoted = DraftBuffer.Quote("Ada <ada@example.test>", body, 10);

        Assert.Contains("> [...]", quoted, StringComparison.Ordinal);
        Assert.True(quoted.Split('\n').Length < 20);
    }

    [Fact]
    public void A_quote_of_hostile_text_is_still_only_quoted_text()
    {
        var quoted = DraftBuffer.Quote("attacker", "\u001b[2Jwiped\u202Egnp.exe", 10);

        foreach (var line in Mailcoded.Tui.Render.TerminalText.Wrap(quoted, 60, 20))
            Assert.DoesNotContain(line.Text, c => c < ' ' || c == '');
    }

    private static IEnumerable<string> Members(Type type)
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        foreach (var property in type.GetProperties(Flags)) yield return property.Name;
        foreach (var field in type.GetFields(Flags)) yield return field.Name;
    }

    private static SendPreviewDto Preview() => new()
    {
        From = "me@example.test",
        To = ["you@example.test"],
        Subject = "hello",
        BodyPreview = "hi",
        MessageId = "abc@example.test",
    };
}
