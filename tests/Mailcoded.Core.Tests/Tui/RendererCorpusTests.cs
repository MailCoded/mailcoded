using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Tui.Render;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

/// <summary>Every field the reader can put on screen, from every fixture, through the renderer.</summary>
public sealed class RendererCorpusTests
{
    private static readonly DateTimeOffset InternalDate = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_field_of_any_fixture_can_carry_an_escape_onto_a_row()
    {
        var checkedFields = 0;

        foreach (var fileName in Fixtures.EmlFileNames())
        {
            var parsed = Parse(fileName);

            foreach (var field in Renderable(parsed))
            {
                AssertSafe(TerminalText.Cell(field, 80).Text, fileName);
                AssertSafe(TerminalText.Cell(field, 7).Text, fileName);
                foreach (var line in TerminalText.Wrap(field, 78, 40)) AssertSafe(line.Text, fileName);
                checkedFields++;
            }
        }

        Assert.True(checkedFields > 100, $"Only {checkedFields} fields were exercised; the corpus should be richer.");
    }

    [Fact]
    public void The_terminal_injection_fixture_reaches_the_screen_defanged()
    {
        var parsed = Parse("037-terminal-injection.eml");

        var subject = TerminalText.Cell(parsed.Subject, 80).Text;
        AssertSafe(subject, "037");
        Assert.Equal("[2J [HCleared your screen", subject);

        AssertSafe(TerminalText.Cell(parsed.From, 80).Text, "037");

        // The parser strips only Control from a filename; U+202E is Format, so the renderer is the last gate.
        var attachment = Assert.Single(parsed.Attachments);
        var name = TerminalText.Cell(attachment.FileName, 40).Text;

        Assert.DoesNotContain('\u202E', name);
        Assert.Equal("invoice\ufffdfdp.exe", name);
    }

    [Fact]
    public void An_html_only_message_renders_its_text_projection_without_markup_escapes()
    {
        var parsed = Parse("011-html-only.eml");

        foreach (var line in TerminalText.Wrap(parsed.BodyText, 78, 40)) AssertSafe(line.Text, "011");
    }

    [Fact]
    public void A_prompt_injection_body_is_still_only_characters_on_a_row()
    {
        var parsed = Parse("026-prompt-injection.eml");
        var lines = TerminalText.Wrap(parsed.BodyText, 78, 40);

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            AssertSafe(line.Text, "026");
            Assert.True(line.Columns <= 78);
        }
    }

    private static IEnumerable<string?> Renderable(ParsedMessage parsed)
    {
        yield return parsed.Subject;
        yield return parsed.From;
        yield return parsed.To;
        yield return parsed.Cc;
        yield return parsed.ReplyTo;
        yield return parsed.Sender;
        yield return parsed.BodyText;

        foreach (var attachment in parsed.Attachments)
        {
            yield return attachment.FileName;
            yield return attachment.MimeType;
        }

        foreach (var warning in parsed.ParseWarnings) yield return warning;
    }

    private static void AssertSafe(string rendered, string origin)
    {
        foreach (var c in rendered)
        {
            Assert.False(
                c < ' ' || c == '\u007f' || c is >= '\u0080' and <= '\u009f',
                $"Fixture '{origin}' put U+{(int)c:X4} on a row. RULE (CLAUDE invariant 4): mail content never "
                + "reaches a terminal unfiltered.");
        }
    }

    private static ParsedMessage Parse(string fileName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = Fixtures.Open(fileName);
        return MessageParser.Default.Parse(stream, InternalDate, ct);
    }
}
