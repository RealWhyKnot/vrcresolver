using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class TerminalBlocksTests
{
    [Fact]
    public void Panel_ColorsReconnectingAsWarning()
    {
        IReadOnlyList<TerminalFrame> frames = TerminalBlocks.Panel(
            "status",
            new[] { ("mesh", "reconnecting", "waiting for backend") },
            width: 80,
            glyphs: TerminalGlyphSet.Ascii);

        TerminalTextRun state = frames
            .SelectMany(static frame => frame.Runs)
            .First(run => run.Text.Contains("reconnecting", StringComparison.Ordinal));

        Assert.Equal(ConsoleColor.Yellow, state.Color);
    }

    [Fact]
    public void Panel_DoesNotTreatNotEligibleAsHealthy()
    {
        IReadOnlyList<TerminalFrame> frames = TerminalBlocks.Panel(
            "helper diagnostics",
            new[]
            {
                ("encoder", "eligible", "hardware encoder available"),
                ("scheduler", "not eligible", "hardware encoder missing"),
            },
            width: 80,
            glyphs: TerminalGlyphSet.Ascii);

        TerminalTextRun eligible = frames
            .SelectMany(static frame => frame.Runs)
            .First(run => run.Text.Contains("eligible", StringComparison.Ordinal)
                && !run.Text.Contains("not eligible", StringComparison.Ordinal));
        TerminalTextRun notEligible = frames
            .SelectMany(static frame => frame.Runs)
            .First(run => run.Text.Contains("not eligible", StringComparison.Ordinal));

        Assert.Equal(ConsoleColor.Green, eligible.Color);
        Assert.Equal(ConsoleColor.Red, notEligible.Color);
    }
}
