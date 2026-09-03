using System.Globalization;
using System.Text;
using Mailcoded.Cli;
using Mailcoded.Core.Parsing;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

/// <summary>CLAUDE invariant 4: mail content must never reach a terminal unfiltered.</summary>
public sealed class TerminalInjectionTests
{
    [Theory]
    [InlineData(0x202E)]
    [InlineData(0x202A)]
    [InlineData(0x2066)]
    [InlineData(0x061C)]
    [InlineData(0x200B)]
    [InlineData(0xFEFF)]
    [InlineData(0x00AD)]
    [InlineData(0x206A)]
    [InlineData(0xE0001)]
    [InlineData(0xE0020)]
    [InlineData(0xE007F)]
    public void Invisible_scalars_never_survive_to_a_terminal(int codePoint)
    {
        var rune = new Rune(codePoint);
        var payload = "a" + rune + "b";

        Assert.True(PlainText.IsInvisible(rune));
        Assert.False(PlainText.IsRenderable(rune));
        Assert.DoesNotContain(rune.ToString(), SafeText.Line(payload, 64), StringComparison.Ordinal);
        Assert.DoesNotContain(rune.ToString(), SafeText.Block(payload, 64), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[2J[HCleared")]
    [InlineData("2J")]
    [InlineData("before\rafter")]
    [InlineData("bell")]
    [InlineData("del")]
    public void Control_sequences_never_survive_to_a_terminal(string hostile)
    {
        foreach (var rendered in new[] { SafeText.Line(hostile, 64), SafeText.Block(hostile, 64) })
        {
            Assert.DoesNotContain('', rendered);
            Assert.DoesNotContain('', rendered);
            Assert.DoesNotContain('\r', rendered);
            Assert.DoesNotContain('', rendered);
            Assert.DoesNotContain('', rendered);
        }
    }

    [Fact]
    public void Clipping_never_splits_a_surrogate_pair()
    {
        var payload = new string('a', 8) + "\U0001F600" + "tail";

        for (var budget = 1; budget <= payload.Length; budget++)
        {
            foreach (var rendered in new[] { SafeText.Line(payload, budget), SafeText.Block(payload, budget) })
            {
                for (var i = 0; i < rendered.Length; i++)
                {
                    if (!char.IsSurrogate(rendered[i])) continue;

                    var paired = (i + 1 < rendered.Length && char.IsSurrogatePair(rendered[i], rendered[i + 1]))
                        || (i > 0 && char.IsSurrogatePair(rendered[i - 1], rendered[i]));

                    Assert.True(
                        paired,
                        "budget " + budget.ToString(CultureInfo.InvariantCulture) + " emitted a lone surrogate");
                }
            }
        }
    }
}
